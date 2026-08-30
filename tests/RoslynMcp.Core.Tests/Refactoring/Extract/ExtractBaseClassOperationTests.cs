using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractBaseClassOperation"/>, including optional
/// <c>line</c>, <c>column</c>, <c>separateFile</c>, <c>targetFile</c>, and <c>makeAbstract</c>.
/// </summary>
public class ExtractBaseClassOperationTests
{
    private const string EmployeeSource = """
        namespace TestApp;

        public class Employee
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public void Work() { }
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
            public /* first-person */ class Person
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
        var @params = new ExtractBaseClassParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" }
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractBaseClassOperation.Validate(new ExtractBaseClassParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractBaseClassOperation.Validate(new ExtractBaseClassParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeInheritsFrom(types[0], "PersonBase"));
        Assert.False(TypeInheritsFrom(types[1], "PersonBase"));
        Assert.NotNull(FindProperty(updated, "PersonBase", "Name"));
        Assert.Null(FindProperty(updated, "PersonBase", "Age"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Person", 1, "Age"));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "NestedPersonBase",
            Members = new[] { "Age" },
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeInheritsFrom(types[0], "NestedPersonBase"));
        Assert.True(TypeInheritsFrom(types[1], "NestedPersonBase"));
        Assert.Null(FindProperty(updated, "NestedPersonBase", "Name"));
        Assert.NotNull(FindProperty(updated, "NestedPersonBase", "Age"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Person", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 1, "Age"));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" },
            Line = FindLine(NestedSameNamePersonSource, "outer-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeInheritsFrom(types[0], "PersonBase"));
        Assert.False(TypeInheritsFrom(types[1], "PersonBase"));
        Assert.NotNull(FindProperty(updated, "PersonBase", "Name"));
        Assert.Null(FindProperty(updated, "PersonBase", "Age"));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = FindLine(EnumFirstThenSameNamedClassSource, "person-enum")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("class PersonBase", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_LineOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("class PersonBase", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SequentialExtracts_ReusedWorkspace_InsertsOnSecondSelectedClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(LaterSameNamedPersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "FirstBase",
            Members = new[] { "Title" },
            Line = FindLine(LaterSameNamedPersonSource, "first-person")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var second = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "SecondBase",
            Members = new[] { "Name" },
            Line = FindLine(afterFirst, "later-person")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeInheritsFrom(types[0], "FirstBase"));
        Assert.False(TypeInheritsFrom(types[0], "SecondBase"));
        Assert.True(TypeInheritsFrom(types[1], "SecondBase"));
        Assert.False(TypeInheritsFrom(types[1], "FirstBase"));
        Assert.NotNull(FindProperty(updated, "FirstBase", "Title"));
        Assert.NotNull(FindProperty(updated, "SecondBase", "Name"));
        Assert.Null(FindProperty(updated, "FirstBase", "Name"));
        Assert.Null(FindProperty(updated, "SecondBase", "Title"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 0, "Title"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 1, "Name"));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Line_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "NestedPersonBase",
            Members = new[] { "Age" },
            Line = FindLine(NestedSameNamePersonSource, "nested-person"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Extract base class NestedPersonBase", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.DoesNotContain("Name", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].AfterSnippet);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Update Person to inherit from NestedPersonBase", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "nested-person"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
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

        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_StructFirstPicksClass()
    {
        const string source = """
            namespace Other
            {
                public struct Person
                {
                    public int Id;
                }
            }

            namespace TestApp
            {
                public class Person
                {
                    public string Name { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", FindLine(EnumFirstThenSameNamedClassSource, "person-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", FindLine(EnumFirstThenSameNamedClassSource, "person-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
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

        Assert.True(ExtractBaseClassOperation.SpanCoversLine(span, 1));
        Assert.True(ExtractBaseClassOperation.SpanCoversLine(span, 2));
        Assert.False(ExtractBaseClassOperation.SpanCoversLine(span, 3));
        Assert.False(ExtractBaseClassOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_OmittedLine_EnumFirstThenSameNamedClass_ExtractsFromClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Single(types);
        Assert.True(TypeInheritsFrom(types[0], "PersonBase"));
        Assert.Contains("class PersonBase", updated);
        Assert.Contains("enum Person", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("class PersonBase", updated[..updated.IndexOf("enum Person", StringComparison.Ordinal)]);
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedPersonSource = """
        namespace TestApp;

        public class Person { public string Name { get; set; } public class Person { public int Age { get; set; } } }
        """;

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new ExtractBaseClassParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" }
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractBaseClassOperation.Validate(new ExtractBaseClassParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractBaseClassOperation.Validate(new ExtractBaseClassParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
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
            ExtractBaseClassOperation.Validate(new ExtractBaseClassParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = typeName,
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeInheritsFrom(types[0], "PersonBase"));
        Assert.False(TypeInheritsFrom(types[1], "PersonBase"));
        Assert.NotNull(FindProperty(updated, "PersonBase", "Name"));
        Assert.Null(FindProperty(updated, "PersonBase", "Age"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Person", 1, "Age"));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "NestedPersonBase",
            Members = new[] { "Age" },
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeInheritsFrom(types[0], "NestedPersonBase"));
        Assert.True(TypeInheritsFrom(types[1], "NestedPersonBase"));
        Assert.Null(FindProperty(updated, "NestedPersonBase", "Name"));
        Assert.NotNull(FindProperty(updated, "NestedPersonBase", "Age"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Person", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 1, "Age"));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "NestedPersonBase",
            Members = new[] { "Age" },
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeInheritsFrom(types[0], "NestedPersonBase"));
        Assert.True(TypeInheritsFrom(types[1], "NestedPersonBase"));
        Assert.Null(FindProperty(updated, "NestedPersonBase", "Name"));
        Assert.NotNull(FindProperty(updated, "NestedPersonBase", "Age"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Person", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 1, "Age"));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public string Name");

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" },
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeInheritsFrom(types[0], "PersonBase"));
        Assert.False(TypeInheritsFrom(types[1], "PersonBase"));
        Assert.NotNull(FindProperty(updated, "PersonBase", "Name"));
        Assert.Null(FindProperty(updated, "PersonBase", "Age"));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: null, column: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public int Age"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public string Name"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
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
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", line: null, enumColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var delegateColumn = ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()");
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", line: null, delegateColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_StructFirstPicksClass()
    {
        const string source = """
            namespace Other
            {
                public struct Person
                {
                    public int Id;
                }
            }

            namespace TestApp
            {
                public class Person
                {
                    public string Name { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var structColumn = ColumnOf(source, "Person");
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", line: null, structColumn);

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

        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", identifierLine, ColumnOf(source, "Person // split-person"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ColumnOnContinuationLine_PicksType()
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
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Person");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "PersonBase",
            Members = new[] { "Name" },
            Line = identifierLine,
            Column = ColumnOf(source, "Person // split-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Single(types);
        Assert.True(TypeInheritsFrom(types[0], "PersonBase"));
        Assert.NotNull(FindProperty(updated, "PersonBase", "Name"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnEnumIdentifier_PicksEnum()
    {
        const string source = """
            namespace TestApp { public enum Person { Ready } public class Person { public string Name { get; set; } }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var line = FindLine(source, "public enum Person");
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(source, "Person { Ready }"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ColumnOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        const string source = """
            namespace TestApp { public enum Person { Ready } public class Person { public string Name { get; set; } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = FindLine(source, "public enum Person"),
                Column = ColumnOf(source, "Person { Ready }")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("class PersonBase", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(
            root, "Person",
            FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"),
            ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ColumnOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"),
                Column = ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("class PersonBase", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractBaseClassOperation.FindTypeDeclaration(root, "Person", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ColumnAndLine_UnknownTypeName_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                BaseClassName = "PersonBase",
                Members = new[] { "Name" },
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Column_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "NestedPersonBase",
            Members = new[] { "Age" },
            Line = line,
            Column = ColumnOf(SameLineNestedPersonSource, "Person { public int Age"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Extract base class NestedPersonBase", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.DoesNotContain("Name", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].AfterSnippet);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Update Person to inherit from NestedPersonBase", StringComparison.Ordinal));
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

        Assert.True(ExtractBaseClassOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ExtractBaseClassOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ExtractBaseClassOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ExtractBaseClassOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SequentialColumn_ReusedWorkspace_InsertsOnSecondSelectedClass()
    {
        const string source = """
            namespace TestApp;

            public class Person { public string Name { get; set; } public class Person { public int Age { get; set; } } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var line = FindLine(source, "public class Person { public string Name");

        var first = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "OuterPersonBase",
            Members = new[] { "Name" },
            Line = line,
            Column = ColumnOf(source, "Person { public string Name")
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            BaseClassName = "NestedPersonBase",
            Members = new[] { "Age" },
            Line = FindLine(afterFirst, "Person { public int Age"),
            Column = ColumnOf(afterFirst, "Person { public int Age")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeInheritsFrom(types[0], "OuterPersonBase"));
        Assert.False(TypeInheritsFrom(types[0], "NestedPersonBase"));
        Assert.True(TypeInheritsFrom(types[1], "NestedPersonBase"));
        Assert.False(TypeInheritsFrom(types[1], "OuterPersonBase"));
        Assert.NotNull(FindProperty(updated, "OuterPersonBase", "Name"));
        Assert.NotNull(FindProperty(updated, "NestedPersonBase", "Age"));
        Assert.Null(FindProperty(updated, "OuterPersonBase", "Age"));
        Assert.Null(FindProperty(updated, "NestedPersonBase", "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Person", 1, "Age"));
    }

    #endregion

    [SkippableFact]
    public async Task ExtractBaseClass_Default_WritesBaseClassIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.False(File.Exists(sibling));
        Assert.DoesNotContain(sibling, result.Changes!.FilesCreated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileFalse_WritesBaseClassIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_WritesSiblingFileAndRemovesBaseClassFromSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseFile = NormalizeNewlines(await File.ReadAllTextAsync(sibling));

        Assert.DoesNotContain("class Person", source);
        AssertInheritsFrom(source, "Employee", "Person");
        Assert.Contains("class Person", baseFile);
        Assert.Contains("Name", baseFile);
        Assert.Contains("Age", baseFile);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_TargetFileWinsOverSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        var explicitTarget = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "CustomBase.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
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

        Assert.DoesNotContain("class Person", source);
        Assert.Contains("class Person", custom);
        AssertInheritsFrom(source, "Employee", "Person");
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
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
    public async Task ExtractBaseClass_SeparateFileTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Create, result.PendingChanges[0].ChangeType);
        Assert.Equal(sibling, result.PendingChanges[0].File);
        Assert.Contains("Person", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_SiblingExists_ThrowsTargetFileExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        await File.WriteAllTextAsync(sibling, """
            namespace TestApp;

            public class Existing
            {
            }
            """);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var siblingBefore = await File.ReadAllTextAsync(sibling);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Name", "Age" },
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(siblingBefore, await File.ReadAllTextAsync(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_ExplicitCompileItems_AddsCompileInclude()
    {
        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        var projectBefore = await File.ReadAllTextAsync(workspace.ProjectPath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);
        Assert.Contains(workspace.ProjectPath, result.Changes.FilesModified);

        var projectAfter = await File.ReadAllTextAsync(workspace.ProjectPath);
        Assert.NotEqual(projectBefore, projectAfter);
        Assert.Contains("Include=\"Person.cs\"", projectAfter);
        Assert.Contains("Include=\"Employee.cs\"", projectAfter);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("class Person", source);
        AssertInheritsFrom(source, "Employee", "Person");
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_SdkDefaults_LeavesProjectFileUnchanged()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var projectBefore = await File.ReadAllTextAsync(workspace.ProjectPath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.Equal(projectBefore, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.DoesNotContain(workspace.ProjectPath, result.Changes!.FilesModified);
        Assert.DoesNotContain("Include=\"Person.cs\"", projectBefore);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_ExplicitCompileItems_Preview_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var projectBefore = await File.ReadAllTextAsync(workspace.ProjectPath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(projectBefore, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.False(File.Exists(sibling));
        Assert.Contains(result.PendingChanges!, c => c.File == workspace.ProjectPath);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_NestedClass_ThrowsCannotExtractNestedToSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedEmployeeSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Value" },
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.CannotExtractNestedToSeparateFile, ex.ErrorCode);
        Assert.Equal("3142", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Default_NestedClass_WritesBaseClassInsideContainingType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedEmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Value" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("class Outer<T>", updated);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"))));
    }

    #region Events

    [SkippableFact]
    public async Task ExtractBaseClass_FieldLikeEvent_MovesEventOntoBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed;

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public void Work()", employee);
        Assert.DoesNotContain("abstract event", updated);
        Assert.DoesNotContain("public abstract class Person", updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_AccessorStyleEvent_MovesEventOntoBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }

                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.Contains("add", person);
        Assert.Contains("remove", person);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public string Name { get; set; }", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MultiVariableEventField_LeavesUnrelatedDeclarator()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed, Other;

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.DoesNotContain("Other", person);
        Assert.DoesNotContain("Changed, Other", updated);
        Assert.Contains("public event System.EventHandler Other", employee);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public int Age { get; set; }", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MultiVariableEventField_SecondDeclarator_LeavesFirst()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed, Other;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Other" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Other", person);
        Assert.DoesNotContain("Changed", person);
        Assert.Contains("public event System.EventHandler Changed", employee);
        Assert.DoesNotContain("event System.EventHandler Other", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_PrivateEvent_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                private event System.EventHandler Changed;

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("protected event System.EventHandler Changed", person);
        Assert.DoesNotContain("private event", person);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public void Work()", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_PrivateAccessorStyleEvent_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                private event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("protected event System.EventHandler Changed", person);
        Assert.DoesNotContain("private event", person);
        Assert.Contains("add", person);
        Assert.Contains("remove", person);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Event_Preview_WritesNothing_AndDescribesEvent()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed;

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Changed", result.PendingChanges[0].Description);
        Assert.Contains("event", result.PendingChanges[0].AfterSnippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Changed", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"))));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_EventLeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        Assert.Contains("abstract class Person", updated);
        Assert.Contains("abstract event System.EventHandler Changed", person);
        Assert.Contains("override event System.EventHandler Changed", employee);
        AssertInheritsFrom(updated, "Employee", "Person");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Event_LeavesMethodsPropertiesAndFieldsOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public string Name { get; set; }
                public int Age;
                public event System.EventHandler Changed;
                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.DoesNotContain("Name", person);
        Assert.DoesNotContain("Age", person);
        Assert.DoesNotContain("Work", person);
        Assert.Contains("public string Name { get; set; }", employee);
        Assert.Contains("public int Age", employee);
        Assert.Contains("public void Work()", employee);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
    }

    #endregion

    #region Indexers

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

            public void Work() { }
        }
        """;

    [SkippableFact]
    public async Task ExtractBaseClass_Default_PublicIndexer_MovesIndexerOntoBase()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.Empty(FindIndexers(updated, "Lookup"));
        AssertInheritsFrom(updated, "Lookup", "Indexable");
        Assert.Contains("public int Count { get; set; }", GetClassSection(updated, "Lookup"));
        Assert.Contains("public void Work()", GetClassSection(updated, "Lookup"));
        Assert.DoesNotContain("abstract", GetClassSection(updated, "Indexable"));
        Assert.DoesNotContain(
            FindType(updated, "Indexable").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task ExtractBaseClass_MembersFilter_IndexerAliases_MovesOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { memberName }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.Contains("this[int i]", GetClassSection(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Lookup"));
        Assert.Contains("public int Count { get; set; }", GetClassSection(updated, "Lookup"));
        Assert.Contains("public void Work()", GetClassSection(updated, "Lookup"));
        Assert.DoesNotContain("Count", GetClassSection(updated, "Indexable"));
        Assert.DoesNotContain("Work", GetClassSection(updated, "Indexable"));
        AssertInheritsFrom(updated, "Lookup", "Indexable");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_OrdinaryProperty_LeavesIndexerOnDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "Count" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "Indexable", "Count");
        Assert.NotNull(property);
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Empty(FindIndexers(updated, "Indexable"));
        Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Contains("this[int i]", GetClassSection(updated, "Lookup"));
        Assert.DoesNotContain("public int Count", GetClassSection(updated, "Lookup"));
        AssertInheritsFrom(updated, "Lookup", "Indexable");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_GetOnlyIndexer_PreservesGetOnly()
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
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.True(
            indexer.ExpressionBody != null
            || (indexer.AccessorList != null
                && indexer.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))
                && indexer.AccessorList.Accessors.All(a => !a.IsKind(SyntaxKind.SetAccessorDeclaration))));
        Assert.Contains("this[int i]", GetClassSection(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Lookup"));
        Assert.Contains("public int Count { get; set; }", GetClassSection(updated, "Lookup"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_RefIndexer_KeepsRef()
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
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Cell",
            BaseClassName = "Indexable",
            Members = new[] { "this[]", "_value" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.False(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref int this[int i]", updated);
        Assert.Contains("_value", GetClassSection(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Cell"));
        AssertInheritsFrom(updated, "Cell", "Indexable");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_RefReadonlyIndexer_KeepsRefReadonly()
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
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Origin",
            BaseClassName = "Indexable",
            Members = new[] { "this[]", "_value" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.True(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref readonly int this[int i]", updated);
        Assert.Contains("_value", GetClassSection(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Origin"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Indexer_RefKindParameter_Preserved()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[in int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        var parameter = Assert.Single(indexer.ParameterList.Parameters);
        Assert.Contains(parameter.Modifiers, t => t.IsKind(SyntaxKind.InKeyword));
        Assert.Contains("this[in int i]", updated);
        Assert.Empty(FindIndexers(updated, "Lookup"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_InitOnlyIndexer_PreservesInit()
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
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]", "_value" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains("this[int i]", updated);
        Assert.Contains("init", ExtractMemberText(indexer));
        Assert.Contains("_value", GetClassSection(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Lookup"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_PrivateIndexer_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                private string this[int i]
                {
                    get => "";
                    set { }
                }

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.PrivateKeyword));
        Assert.Contains("protected", GetClassSection(updated, "Indexable"));
        Assert.DoesNotContain("private", GetClassSection(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Lookup"));
        Assert.Contains("public void Work()", GetClassSection(updated, "Lookup"));
        AssertInheritsFrom(updated, "Lookup", "Indexable");
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SpecificIndexerDisplay_LeavesOtherIndexerOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }

                public string this[string key]
                {
                    get => key;
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[int i]" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseIndexer = Assert.Single(FindIndexers(updated, "Indexable"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Equal("int", Assert.Single(baseIndexer.ParameterList.Parameters).Type!.ToString());
        Assert.Equal("string", Assert.Single(derivedIndexer.ParameterList.Parameters).Type!.ToString());
        AssertInheritsFrom(updated, "Lookup", "Indexable");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Indexer_Preview_WritesNothing_AndDescribesIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Indexable.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("this[]", result.PendingChanges[0].Description);
        Assert.Contains("this[int i]", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain(
            "this[]",
            result.PendingChanges[0].AfterSnippet?.Replace("this[int i]", "", StringComparison.Ordinal) ?? "");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_IndexerLeavesOverrideOnDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseIndexer = Assert.Single(FindIndexers(updated, "Indexable"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Contains("abstract class Indexable", updated);
        Assert.Contains(baseIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.DoesNotContain(baseIndexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Null(baseIndexer.ExpressionBody);
        Assert.All(baseIndexer.AccessorList!.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains(derivedIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("get =>", GetClassSection(updated, "Lookup"));
        Assert.DoesNotContain("get =>", GetClassSection(updated, "Indexable"));
        AssertInheritsFrom(updated, "Lookup", "Indexable");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Indexer_UnknownName_ThrowsMemberNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                BaseClassName = "Indexable",
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Equal("2012", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Indexer_LeavesMethodsPropertiesFieldsAndEventsOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public string Name { get; set; }
                public int Age;
                public event System.EventHandler Changed;
                public string this[int i]
                {
                    get => "";
                    set { }
                }
                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "Item" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexable = GetClassSection(updated, "Indexable");
        var lookup = GetClassSection(updated, "Lookup");

        Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Lookup"));
        Assert.DoesNotContain("Name", indexable);
        Assert.DoesNotContain("Age", indexable);
        Assert.DoesNotContain("Changed", indexable);
        Assert.DoesNotContain("Work", indexable);
        Assert.Contains("public string Name { get; set; }", lookup);
        Assert.Contains("public int Age", lookup);
        Assert.Contains("public event System.EventHandler Changed", lookup);
        Assert.Contains("public void Work()", lookup);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_PrivateProtectedIndexer_BecomesSingleProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                private protected string this[int i]
                {
                    get => "";
                    set { }
                }

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Indexable"));
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.PrivateKeyword));
        Assert.Equal(1, indexer.Modifiers.Count(t => t.IsKind(SyntaxKind.ProtectedKeyword)));
        Assert.DoesNotContain("protected protected", updated);
        Assert.DoesNotContain("private protected", GetClassSection(updated, "Indexable"));
        Assert.Empty(FindIndexers(updated, "Lookup"));
        AssertInheritsFrom(updated, "Lookup", "Indexable");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_ExplicitInterfaceIndexer_ThrowsMemberNotFound()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public class Lookup : ILookup
            {
                string ILookup.this[int i] => "";

                public int Count { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        foreach (var memberName in new[] { "this[]", "Item", "this[int i]" })
        {
            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new ExtractBaseClassParams
                {
                    SourceFile = workspace.SourcePath,
                    TypeName = "Lookup",
                    BaseClassName = "Indexable",
                    Members = new[] { memberName }
                }));

            Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
            Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        }
    }

    [SkippableFact]
    public async Task ExtractBaseClass_OrdinaryIndexer_LeavesExplicitInterfaceIndexerOnDerived()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public interface IOther
            {
                string this[int i] { get; }
            }

            public class Lookup : ILookup, IOther
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }

                string IOther.this[int i] => this[i];
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            BaseClassName = "Indexable",
            Members = new[] { "this[]" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseIndexer = Assert.Single(FindIndexers(updated, "Indexable"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Null(baseIndexer.ExplicitInterfaceSpecifier);
        Assert.Equal("IOther", derivedIndexer.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.DoesNotContain("ILookup.this", GetClassSection(updated, "Indexable"));
        AssertInheritsFrom(updated, "Lookup", "Indexable");
        AssertCompiles(updated);
    }

    #endregion

    #region MakeAbstract

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_MethodLeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public int Work()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Work" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");
        Assert.Contains("abstract class Person", updated);
        Assert.Contains("abstract", person);
        Assert.Contains("Work", person);
        Assert.DoesNotContain("return 1", person);
        Assert.Contains("override", employee);
        Assert.Contains("return 1", employee);
        AssertInheritsFrom(updated, "Employee", "Person");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_PropertyLeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var personProp = FindProperty(updated, "Person", "Name");
        var employeeProp = FindProperty(updated, "Employee", "Name");
        Assert.NotNull(personProp);
        Assert.NotNull(employeeProp);
        Assert.Contains("abstract class Person", updated);
        Assert.Contains(personProp!.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.All(personProp.AccessorList!.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains(employeeProp!.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        AssertInheritsFrom(updated, "Employee", "Person");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_InitializedProperty_DropsInitializerOnBase_KeepsOnOverride()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public string Name { get; set; } = "";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var personProp = FindProperty(updated, "Person", "Name");
        var employeeProp = FindProperty(updated, "Employee", "Name");
        Assert.NotNull(personProp);
        Assert.NotNull(employeeProp);
        Assert.Contains(personProp!.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.Null(personProp.Initializer);
        Assert.Contains(employeeProp!.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.NotNull(employeeProp.Initializer);
        Assert.Equal("\"\"", employeeProp.Initializer!.Value.ToString());
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_AsyncMethod_StripsAsyncOnBase_KeepsOnOverride()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public async System.Threading.Tasks.Task Work()
                {
                    await System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Work" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");
        Assert.Contains("abstract", person);
        Assert.Contains("Work", person);
        Assert.DoesNotContain("async", person);
        Assert.Contains("async", employee);
        Assert.Contains("override", employee);
        Assert.Contains("await", employee);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstractFalse_MethodStillMoves()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public int Work()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Work" },
            MakeAbstract = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");
        Assert.DoesNotContain("abstract class Person", updated);
        Assert.Contains("Work", person);
        Assert.Contains("return 1", person);
        Assert.DoesNotContain("Work", employee);
        Assert.DoesNotContain("override", employee);
        AssertInheritsFrom(updated, "Employee", "Person");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_FieldStillMovesConcrete()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public int Age;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Age" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");
        Assert.Contains("abstract class Person", updated);
        Assert.Contains("public int Age", person);
        Assert.DoesNotContain("abstract int Age", person);
        Assert.DoesNotContain("Age", employee);
        Assert.DoesNotContain("override", employee);
        AssertInheritsFrom(updated, "Employee", "Person");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_StaticMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public static void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Work" },
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_ExplicitInterfaceMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IWork
            {
                void Work();
            }

            public class Employee : IWork
            {
                void IWork.Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Work" },
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_PrivateAccessorProperty_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public string Name { get; private set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Name" },
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_PrivateAccessorIndexer_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[int i] { get => i; private set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                BaseClassName = "Indexable",
                Members = new[] { "this[]" },
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_ExplicitInterfaceIndexer_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public class Lookup : ILookup
            {
                string ILookup.this[int i] => "";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                BaseClassName = "Indexable",
                Members = new[] { "this[int i]" },
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_Preview_WritesNothing_AndDescribesAbstractAndOverrides()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public int Work()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Work" },
            MakeAbstract = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(
            result.PendingChanges,
            change => change.Description.Contains("abstract", StringComparison.OrdinalIgnoreCase)
                && change.Description.Contains("Work", StringComparison.Ordinal));
        Assert.Contains(
            result.PendingChanges,
            change => change.Description.Contains("override", StringComparison.OrdinalIgnoreCase)
                && change.Description.Contains("Work", StringComparison.Ordinal));
        Assert.Contains("abstract", result.PendingChanges[0].AfterSnippet, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"))));
    }

    #endregion

    [Fact]
    public void AddExplicitCompileItemIfNeeded_SdkDefaults_Unchanged()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = ExtractBaseClassOperation.AddExplicitCompileItemIfNeeded(
            xml,
            projectDir,
            Path.Combine(projectDir, "Person.cs"));

        Assert.Equal(xml.ReplaceLineEndings(), updated.ReplaceLineEndings());
    }

    [Fact]
    public void AddExplicitCompileItemIfNeeded_ExplicitItems_AddsInclude()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Employee.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = ExtractBaseClassOperation.AddExplicitCompileItemIfNeeded(
            xml,
            projectDir,
            Path.Combine(projectDir, "Person.cs"));

        Assert.Contains("Include=\"Employee.cs\"", updated);
        Assert.Contains("Include=\"Person.cs\"", updated);
    }

    [Fact]
    public void AddExplicitCompileItemIfNeeded_AlreadyIncluded_Unchanged()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Employee.cs" />
                <Compile Include="Person.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = ExtractBaseClassOperation.AddExplicitCompileItemIfNeeded(
            xml,
            projectDir,
            Path.Combine(projectDir, "Person.cs"));

        Assert.Equal(xml.ReplaceLineEndings(), updated.ReplaceLineEndings());
    }

    private const string NestedEmployeeSource = """
        namespace TestApp;

        public class Outer<T>
        {
            public class Employee
            {
                public T Value { get; set; }

                public void Work() { }
            }
        }
        """;

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpExtractBaseClassMissing.cs");

    private static IReadOnlyList<TypeDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeInheritsFrom(TypeDeclarationSyntax type, string baseClassName) =>
        type.BaseList?.Types.Any(t => t.Type.ToString() == baseClassName) == true;

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

    private static PropertyDeclarationSyntax? FindPropertyOnNthType(
        string source,
        string typeName,
        int index,
        string propertyName)
    {
        var types = GetTypes(source, typeName);
        Assert.True(index < types.Count, $"Expected at least {index + 1} type(s) named '{typeName}'.");
        return types[index].Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName);
    }

    /// <summary>
    /// Base-list trivia from <c>WithBaseList</c> may omit spaces; compare a compacted form.
    /// </summary>
    private static void AssertInheritsFrom(string source, string typeName, string baseClassName)
    {
        var compact = new string(source.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains($"class{typeName}:{baseClassName}", compact);
    }

    private static string GetClassSection(string source, string className)
    {
        var marker = $"class {className}";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Class '{className}' not found in:\n{source}");
        var next = source.IndexOf("class ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
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

    private static string ExtractMemberText(MemberDeclarationSyntax member) =>
        NormalizeNewlines(member.NormalizeWhitespace().ToFullString());

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "ExtractBaseClassCompileTest",
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
        Assert.True(errors.Count == 0, "Generated extract_base_class members did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Employee.cs") =>
            CreateAsync("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """, source, fileName);

        public static Task<TempWorkspace> CreateWithExplicitCompileItemsAsync(
            string source,
            string fileName = "Employee.cs") =>
            CreateAsync($"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <EnableDefaultItems>false</EnableDefaultItems>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="{fileName}" />
                  </ItemGroup>
                </Project>
                """, source, fileName);

        public static async Task<TempWorkspace> CreateAsync(string projectXml, string source, string fileName)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractBaseClass_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, projectXml);
            await File.WriteAllTextAsync(sourcePath, source);

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
}

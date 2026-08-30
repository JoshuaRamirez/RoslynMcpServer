using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// Operation-level tests for <see cref="PullMembersUpOperation"/>, including optional
/// <c>line</c>, <c>column</c>, <c>targetBaseType</c>, <c>makeAbstract</c>, and <c>members</c>.
/// </summary>
public class PullMembersUpOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = "",
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMembers_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Derived",
                Members = []
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = "Derived.cs",
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 optional line disambiguation

    private const string NestedSameNameDogSource = """
        namespace TestApp;

        public class Animal
        {
        }

        public class Creature
        {
        }

        public /* outer-dog */ class Dog : Animal
        {
            public string Name { get; set; }

            public /* nested-dog */ class Dog : Creature
            {
                public int Age { get; set; }
            }
        }
        """;

    private const string EnumFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* dog-enum */ enum Dog
            {
                Ready
            }
        }

        namespace TestApp
        {
            public class Animal
            {
            }

            public /* dog-class */ class Dog : Animal
            {
                public string Name { get; set; }
            }
        }
        """;

    private const string DelegateFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* dog-delegate */ delegate void Dog();
        }

        namespace TestApp
        {
            public class Animal
            {
            }

            public /* dog-class */ class Dog : Animal
            {
                public string Name { get; set; }
            }
        }
        """;

    private const string LaterSameNamedDogSource = """
        namespace Other
        {
            public class Animal
            {
            }

            public /* first-dog */ class Dog : Animal
            {
                public string Title { get; set; }
            }
        }

        namespace TestApp
        {
            public class Creature
            {
            }

            public /* later-dog */ class Dog : Creature
            {
                public string Name { get; set; }
            }
        }
        """;

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new PullMembersUpParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Dog",
            Members = ["Name"]
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
                Members = ["Name"],
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
                Members = ["Name"],
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindProperty(updated, "Animal", "Name"));
        Assert.Null(FindProperty(updated, "Creature", "Age"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    [SkippableFact]
    public async Task PullMembersUp_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Age"],
            Line = FindLine(NestedSameNameDogSource, "nested-dog")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.Null(FindProperty(updated, "Animal", "Name"));
        Assert.NotNull(FindProperty(updated, "Creature", "Age"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    [SkippableFact]
    public async Task PullMembersUp_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"],
            Line = FindLine(NestedSameNameDogSource, "outer-dog")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindProperty(updated, "Animal", "Name"));
        Assert.Null(FindProperty(updated, "Creature", "Age"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    [SkippableFact]
    public async Task PullMembersUp_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Name"],
                Line = FindLine(EnumFirstThenSameNamedClassSource, "dog-enum")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Animal"));
    }

    [SkippableFact]
    public async Task PullMembersUp_LineOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Name"],
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Animal"));
    }

    [SkippableFact]
    public async Task PullMembersUp_SequentialPulls_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(LaterSameNamedDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Title"],
            Line = FindLine(LaterSameNamedDogSource, "first-dog")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var second = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"],
            Line = FindLine(afterFirst, "later-dog")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindProperty(updated, "Animal", "Title"));
        Assert.NotNull(FindProperty(updated, "Creature", "Name"));
        Assert.Null(FindProperty(updated, "Animal", "Name"));
        Assert.Null(FindProperty(updated, "Creature", "Title"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 0, "Title"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 1, "Name"));
    }

    [SkippableFact]
    public async Task PullMembersUp_Line_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Age"],
            Line = FindLine(NestedSameNameDogSource, "nested-dog"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Age", StringComparison.Ordinal)
            && c.Description.Contains("Creature", StringComparison.Ordinal));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("Age", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, c =>
            c.Description.Contains("Name", StringComparison.Ordinal)
            && c.Description.Contains("Animal", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameDogSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameDogSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", FindLine(NestedSameNameDogSource, "nested-dog"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Dog");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameDogSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", FindLine(NestedSameNameDogSource, "outer-dog"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Dog // split-dog
            {
                public string Name { get; set; }

                public class Dog // nested-dog
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-dog");
        Assert.NotEqual(startLine, identifierLine);

        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameDogSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_StructFirstPicksStruct()
    {
        const string source = """
            namespace Other
            {
                public struct Dog
                {
                    public int Id;
                }
            }

            namespace TestApp
            {
                public class Dog
                {
                    public string Name { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: null);

        Assert.NotNull(found);
        Assert.IsType<StructDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", FindLine(EnumFirstThenSameNamedClassSource, "dog-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", FindLine(EnumFirstThenSameNamedClassSource, "dog-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate"));

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

        Assert.True(PullMembersUpOperation.SpanCoversLine(span, 1));
        Assert.True(PullMembersUpOperation.SpanCoversLine(span, 2));
        Assert.False(PullMembersUpOperation.SpanCoversLine(span, 3));
        Assert.False(PullMembersUpOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task PullMembersUp_OmittedLine_EnumFirstThenSameNamedClass_PullsFromClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Single(types);
        Assert.NotNull(FindProperty(updated, "Animal", "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.Contains("enum Dog", updated, StringComparison.Ordinal);
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedDogSource = """
        namespace TestApp;

        public class Animal
        {
        }

        public class Creature
        {
        }

        public class Dog : Animal { public string Name { get; set; } public class Dog : Creature { public int Age { get; set; } } }
        """;

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new PullMembersUpParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Dog",
            Members = ["Name"]
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
                Members = ["Name"],
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
                Members = ["Name"],
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
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = typeName,
                Members = ["Name"],
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindProperty(updated, "Animal", "Name"));
        Assert.Null(FindProperty(updated, "Creature", "Age"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    [SkippableFact]
    public async Task PullMembersUp_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Age"],
            Line = FindLine(NestedSameNameDogSource, "nested-dog")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.Null(FindProperty(updated, "Animal", "Name"));
        Assert.NotNull(FindProperty(updated, "Creature", "Age"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    [SkippableFact]
    public async Task PullMembersUp_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public string Name");
        var column = ColumnOf(SameLineNestedDogSource, "Dog : Creature");

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Age"],
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.Null(FindProperty(updated, "Animal", "Name"));
        Assert.NotNull(FindProperty(updated, "Creature", "Age"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    [SkippableFact]
    public async Task PullMembersUp_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public string Name");
        var column = ColumnOf(SameLineNestedDogSource, "Dog : Animal");

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"],
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindProperty(updated, "Animal", "Name"));
        Assert.Null(FindProperty(updated, "Creature", "Age"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameDogSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: null, column: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedDogSource).GetRoot();
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public string Name");
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", line, ColumnOf(SameLineNestedDogSource, "Dog : Creature"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Dog");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedDogSource).GetRoot();
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public string Name");
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", line, ColumnOf(SameLineNestedDogSource, "Dog : Animal"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedDogSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedDogSource, "Dog : Creature");
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var enumColumn = ColumnOf(EnumFirstThenSameNamedClassSource, "Dog");
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", line: null, enumColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var delegateColumn = ColumnOf(DelegateFirstThenSameNamedClassSource, "Dog()");
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", line: null, delegateColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_StructFirstPicksStruct()
    {
        const string source = """
            namespace Other
            {
                public struct Dog
                {
                    public int Id;
                }
            }

            namespace TestApp
            {
                public class Dog
                {
                    public string Name { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var structColumn = ColumnOf(source, "Dog");
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", line: null, structColumn);

        Assert.NotNull(found);
        Assert.IsType<StructDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Dog // split-dog
            {
                public string Name { get; set; }

                public class Dog // nested-dog
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-dog");
        Assert.NotEqual(startLine, identifierLine);

        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", identifierLine, ColumnOf(source, "Dog // split-dog"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task PullMembersUp_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class
                Dog // split-dog
                : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Dog");
        var identifierLine = FindLine(source, "split-dog");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"],
            Line = identifierLine,
            Column = ColumnOf(source, "Dog // split-dog")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.NotNull(FindProperty(updated, "Animal", "Name"));
        Assert.Null(FindProperty(updated, "Dog", "Name"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnEnumIdentifier_PicksEnum()
    {
        const string source = """
            namespace TestApp { public enum Dog { Ready } public class Dog { public string Name { get; set; } }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var line = FindLine(source, "public enum Dog");
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog", line, ColumnOf(source, "Dog { Ready }"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task PullMembersUp_ColumnOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        const string source = """
            namespace TestApp { public class Animal { } public enum Dog { Ready } public class Dog : Animal { public string Name { get; set; } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Name"],
                Line = FindLine(source, "public enum Dog"),
                Column = ColumnOf(source, "Dog { Ready }")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Animal"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(
            root, "Dog",
            FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate"),
            ColumnOf(DelegateFirstThenSameNamedClassSource, "Dog()"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task PullMembersUp_ColumnOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Name"],
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate"),
                Column = ColumnOf(DelegateFirstThenSameNamedClassSource, "Dog()")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Animal"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameDogSource).GetRoot();
        var found = PullMembersUpOperation.FindTypeDeclaration(root, "Dog", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task PullMembersUp_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Name"],
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_ColumnAndLine_UnknownTypeName_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                Members = ["Name"],
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_Column_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public string Name");

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Age"],
            Line = line,
            Column = ColumnOf(SameLineNestedDogSource, "Dog : Creature"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Age", StringComparison.Ordinal)
            && c.Description.Contains("Creature", StringComparison.Ordinal));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("Age", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, c =>
            c.Description.Contains("Name", StringComparison.Ordinal)
            && c.Description.Contains("Animal", StringComparison.Ordinal));
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

        Assert.True(PullMembersUpOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(PullMembersUpOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(PullMembersUpOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(PullMembersUpOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task PullMembersUp_SequentialColumn_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public string Name");

        var first = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"],
            Line = line,
            Column = ColumnOf(SameLineNestedDogSource, "Dog : Animal")
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Age"],
            Line = FindLine(afterFirst, "Dog : Creature"),
            Column = ColumnOf(afterFirst, "Dog : Creature")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Dog");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindProperty(updated, "Animal", "Name"));
        Assert.NotNull(FindProperty(updated, "Creature", "Age"));
        Assert.Null(FindProperty(updated, "Animal", "Age"));
        Assert.Null(FindProperty(updated, "Creature", "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Dog", 1, "Age"));
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task PullMembersUp_MethodToBaseClass_MovesMemberAndMakesVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"]
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Speak", animal);
        Assert.Contains("virtual", animal);
        Assert.DoesNotContain("Speak", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PropertyToBaseClass_MovesProperty()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Name", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Name", ExtractTypeBody(text, "Dog"));
    }

    [SkippableFact]
    public async Task PullMembersUp_MultipleMembers_MovesAll()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";

                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name", "Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Name", animal);
        Assert.Contains("Speak", animal);
        Assert.DoesNotContain("Name", dog);
        Assert.DoesNotContain("Speak", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateMethod_BecomesProtectedVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private void Log()
                {
                    System.Console.WriteLine("log");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Log"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        Assert.Contains("protected", animal);
        Assert.Contains("virtual", animal);
        Assert.Contains("void Log()", animal);
        Assert.DoesNotContain("private", animal);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_LeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract", text);
        Assert.Contains("abstract", animal);
        Assert.Contains("Speak", animal);
        Assert.DoesNotContain("return 1", animal);
        Assert.Contains("override", dog);
        Assert.Contains("return 1", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MethodToInterface_AddsSignatureAndKeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var iface = ExtractTypeBody(text, "IAnimal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Speak", iface);
        Assert.Contains(";", iface);
        Assert.DoesNotContain("return 1", iface);
        Assert.Contains("Speak", dog);
        Assert.Contains("return 1", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.AfterSnippet != null && c.AfterSnippet.Contains("Speak"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PullMembersUp_FieldLikeEvent_MovesEventOntoBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public event System.EventHandler Changed", animal);
        Assert.DoesNotContain("virtual", animal);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_AccessorStyleEvent_MovesEventOntoBaseAsVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public virtual event System.EventHandler Changed", animal);
        Assert.Contains("add", animal);
        Assert.Contains("remove", animal);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MultiVariableEventField_LeavesUnrelatedDeclarator()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed, Other;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public event System.EventHandler Changed", animal);
        Assert.DoesNotContain("Other", animal);
        Assert.DoesNotContain("Changed, Other", text);
        Assert.Contains("public event System.EventHandler Other", dog);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateEvent_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        Assert.Contains("protected event System.EventHandler Changed", animal);
        Assert.DoesNotContain("private event", animal);
        Assert.DoesNotContain("virtual", animal);
        Assert.DoesNotContain("event System.EventHandler Changed", ExtractTypeBody(text, "Dog"));
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_EventLeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract", text);
        Assert.Contains("abstract event System.EventHandler Changed", animal);
        Assert.Contains("override event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_AccessorStyleEventLeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract event System.EventHandler Changed", animal);
        Assert.DoesNotContain("add", animal);
        Assert.Contains("override event System.EventHandler Changed", dog);
        Assert.Contains("add", dog);
        Assert.Contains("remove", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_MultiVariableEventField_LeavesUnrelatedAndOverridesSelected()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed, Other;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract event System.EventHandler Changed", animal);
        Assert.DoesNotContain("Other", animal);
        Assert.Contains("override event System.EventHandler Changed", dog);
        Assert.Contains("public event System.EventHandler Other", dog);
        Assert.DoesNotContain("Changed, Other", text);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_StaticEvent_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public static event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Changed"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_ExplicitInterfaceEvent_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface INotify
            {
                event System.EventHandler Changed;
            }

            public class Animal
            {
            }

            public class Dog : Animal, INotify
            {
                event System.EventHandler INotify.Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Changed"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MultiVariableEventField_IgnoresUnrelatedDeclaratorDependency()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.Action Selected, Other = DerivedHandler;

                private static void DerivedHandler() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Selected"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("event System.Action Selected", animal);
        Assert.DoesNotContain("Other", animal);
        Assert.Contains("Other", dog);
        Assert.Contains("DerivedHandler", dog);
        Assert.DoesNotContain("Selected", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_EventToInterface_AddsSignatureAndKeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var iface = ExtractTypeBody(text, "IAnimal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("event System.EventHandler Changed", iface);
        Assert.DoesNotContain("public event", iface);
        Assert.Contains("public event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_Event_Preview_WritesNothing_AndDescribesEvent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.Description != null && c.Description.Contains("Changed"));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("event", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet.Contains("Changed"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PullMembersUp_Event_LeavesMethodsPropertiesAndFieldsOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";
                public int Age;
                public event System.EventHandler Changed;

                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public event System.EventHandler Changed", animal);
        Assert.DoesNotContain("Name", animal);
        Assert.DoesNotContain("Age", animal);
        Assert.DoesNotContain("Speak", animal);
        Assert.Contains("public string Name { get; set; }", dog);
        Assert.Contains("public int Age", dog);
        Assert.Contains("public int Speak()", dog);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_ExplicitTarget_UsesNamedBase()
    {
        const string source = """
            namespace TestApp;

            public class Creature
            {
            }

            public class Animal : Creature
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            TargetBaseType = "Creature"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Speak", ExtractTypeBody(text, "Creature"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Dog"));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task PullMembersUp_NoBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.NoCommonBase, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MissingNamedBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"],
                TargetBaseType = "IDisposable"
            }));

        Assert.Equal(ErrorCodes.BaseClassNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_NameConflict_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 0;
                }
            }

            public class Dog : Animal
            {
                public new int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_SignatureConflict_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public void Log(string message)
                {
                }
            }

            public class Dog : Animal
            {
                public void Log(string message)
                {
                    System.Console.WriteLine(message);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_DependsOnDerivedField_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private int _age;

                public int Age()
                {
                    return _age;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Age"]
            }));

        Assert.Equal(ErrorCodes.MemberDependsOnDerived, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_FieldToInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int Age;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Age"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateMethodToInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                private void Log()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_ExternalBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog : System.Exception
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.BaseClassNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MemberNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Missing"]
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
    }

    #endregion

    #region Indexers

    private const string MixedIndexerSource = """
        namespace TestApp;

        public class Animal
        {
        }

        public class Dog : Animal
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
    public async Task PullMembersUp_Default_PublicIndexer_MovesIndexerOntoBase()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Empty(FindIndexers(updated, "Dog"));
        Assert.Contains("public int Count { get; set; }", GetTypeSection(updated, "Dog"));
        Assert.Contains("public void Work()", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain(
            FindType(updated, "Animal").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task PullMembersUp_MembersFilter_IndexerAliases_MovesOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = [memberName]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Animal"));
        Assert.Empty(FindIndexers(updated, "Dog"));
        Assert.Contains("public int Count { get; set; }", GetTypeSection(updated, "Dog"));
        Assert.Contains("public void Work()", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("Count", GetTypeSection(updated, "Animal"));
        Assert.DoesNotContain("Work", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_OrdinaryProperty_LeavesIndexerOnDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Count"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "Animal", "Count");
        Assert.NotNull(property);
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Empty(FindIndexers(updated, "Animal"));
        Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("public int Count", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_GetOnlyIndexer_PreservesGetOnly()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.True(
            indexer.ExpressionBody != null
            || (indexer.AccessorList != null
                && indexer.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))
                && indexer.AccessorList.Accessors.All(a => !a.IsKind(SyntaxKind.SetAccessorDeclaration))));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Animal"));
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_RefIndexer_KeepsRef()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected int _value;
            }

            public class Dog : Animal
            {
                public ref int this[int i] => ref _value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.False(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref int this[int i]", updated);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_RefKindParameter_Preserved()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[in int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        var parameter = Assert.Single(indexer.ParameterList.Parameters);
        Assert.Contains(parameter.Modifiers, t => t.IsKind(SyntaxKind.InKeyword));
        Assert.Contains("this[in int i]", updated);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_InitOnlyIndexer_PreservesInit()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[int i]
                {
                    get => 0;
                    init { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.Contains("this[int i]", updated);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateIndexer_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.PrivateKeyword));
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_SpecificIndexerDisplay_LeavesOtherIndexerOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }

                public string this[string key]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[int i]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Equal("int", Assert.Single(baseIndexer.ParameterList.Parameters).Type!.ToString());
        Assert.Equal("string", Assert.Single(derivedIndexer.ParameterList.Parameters).Type!.ToString());
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_IndexerToInterface_AddsSignatureAndKeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ifaceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Empty(ifaceIndexer.Modifiers);
        Assert.Null(ifaceIndexer.ExpressionBody);
        Assert.Contains(ifaceIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(ifaceIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.All(ifaceIndexer.AccessorList.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains("this[int i]", GetTypeSection(updated, "IAnimal"));
        Assert.DoesNotContain("return", GetTypeSection(updated, "IAnimal"));
        Assert.NotNull(dogIndexer.AccessorList);
        Assert.Contains("get =>", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain(
            FindType(updated, "IAnimal").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_ExplicitInterfaceIndexer_ThrowsMemberNotFound()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public class Animal
            {
            }

            public class Dog : Animal, ILookup
            {
                string ILookup.this[int i] => "";

                public int Count { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        foreach (var memberName in new[] { "this[]", "Item", "this[int i]" })
        {
            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new PullMembersUpParams
                {
                    SourceFile = workspace.SourcePath,
                    TypeName = "Dog",
                    Members = [memberName]
                }));

            Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
            Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        }
    }

    [SkippableFact]
    public async Task PullMembersUp_OrdinaryIndexer_LeavesExplicitInterfaceIndexerOnDerived()
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

            public class Animal
            {
            }

            public class Dog : Animal, ILookup, IOther
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }

                string IOther.this[int i] => this[i];
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Null(baseIndexer.ExplicitInterfaceSpecifier);
        Assert.Equal("IOther", derivedIndexer.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.DoesNotContain("ILookup.this", GetTypeSection(updated, "Animal"));
        Assert.DoesNotContain("IOther.this", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_LeavesMethodsPropertiesFieldsAndEventsOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";
                public int Age;
                public event System.EventHandler Changed;

                public string this[int i]
                {
                    get => "";
                    set { }
                }

                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var animal = ExtractTypeBody(updated, "Animal");
        var dog = ExtractTypeBody(updated, "Dog");
        Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Empty(FindIndexers(updated, "Dog"));
        Assert.Contains("Name", dog);
        Assert.Contains("Age", dog);
        Assert.Contains("Changed", dog);
        Assert.Contains("Speak", dog);
        Assert.DoesNotContain("Name", animal);
        Assert.DoesNotContain("Age", animal);
        Assert.DoesNotContain("Changed", animal);
        Assert.DoesNotContain("Speak", animal);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_Preview_WritesNothing_AndDescribesIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"],
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.Description != null && c.Description.Contains("this[]"));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("this[int i]"));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            !c.AfterSnippet.Replace("this[int i]", "", StringComparison.Ordinal).Contains("this[]"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_UnknownName_ThrowsMemberNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[string key]"]
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_IndexerLeavesOverrideOnDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var animalIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains(FindType(updated, "Animal").Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.Contains(animalIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.DoesNotContain(animalIndexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Null(animalIndexer.ExpressionBody);
        Assert.All(animalIndexer.AccessorList!.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("get =>", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("get =>", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_IndexerToInterface_KeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ifaceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Empty(ifaceIndexer.Modifiers);
        Assert.DoesNotContain(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.DoesNotContain(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.Contains("get =>", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_ExplicitInterfaceIndexer_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public abstract class Animal : ILookup
            {
            }

            public class Dog : Animal
            {
                string ILookup.this[int i] => "";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[int i]"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_IndexerToInterface_PrivateSetter_EmitsGetOnlyAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int this[int i] { get => i; private set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ifaceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains(ifaceIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(ifaceIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.DoesNotContain(ifaceIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.Contains(dogIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(dogIndexer.AccessorList.Accessors, a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration)
            && a.Modifiers.Any(SyntaxKind.PrivateKeyword));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_PrivateSetter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[int i] { get => i; private set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[]"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_SelectedIndexer_DependsOnOtherIndexerOverload_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string this[int i] => this[i.ToString()];

                public string this[string key] => key;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[int i]"]
            }));

        Assert.Equal(ErrorCodes.MemberDependsOnDerived, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_BothIndexerOverloads_AllowsCrossReference()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string this[int i] => this[i.ToString()];

                public string this[string key] => key;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[int i]", "this[string key]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(2, FindIndexers(updated, "Animal").Count);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    #endregion

    #region CS0507 makeAbstract overrides

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_SameAssembly_KeepsProtectedInternalMethod()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                protected internal virtual void Speak() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected internal override void Speak()", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("protected override void Speak()", GetTypeSection(updated, "Dog"));
        Assert.Contains("protected internal abstract void Speak()", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_SameAssembly_KeepsProtectedInternalProperty()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                protected internal int Width { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Width"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var dog = GetTypeSection(updated, "Dog");
        Assert.Contains("protected internal override int Width", dog);
        Assert.DoesNotContain("protected override int Width", dog);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_SameAssembly_KeepsProtectedInternalEvent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                protected internal virtual event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var dog = GetTypeSection(updated, "Dog");
        Assert.Contains("protected internal override event", dog);
        Assert.DoesNotContain("protected override event", dog);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_SameAssembly_KeepsProtectedInternalIndexer()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                protected internal string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("protected internal override", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_SameAssembly_ProtectedInternalProperty_ProtectedSetter_KeepsBoth()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                protected internal int Width { get; protected set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Width"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var dog = GetTypeSection(updated, "Dog");
        Assert.Contains("protected internal override int Width", dog);
        Assert.Contains("protected set", dog);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_CrossAssembly_ProtectedInternalMethod_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
                protected internal virtual void Speak() { }
            }
            """);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.DerivedPath,
            TypeName = "Dog",
            Members = ["Speak"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        Assert.Contains("protected override void Speak()", GetTypeSection(derived, "Dog"));
        Assert.DoesNotContain("protected internal override", derived);
        var library = NormalizeNewlines(await File.ReadAllTextAsync(workspace.LibraryPath));
        Assert.Contains("protected internal abstract void Speak()", GetTypeSection(library, "Animal"));
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_CrossAssembly_ProtectedInternalProperty_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
                protected internal int Width { get; set; }
            }
            """);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.DerivedPath,
            TypeName = "Dog",
            Members = ["Width"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        var dog = GetTypeSection(derived, "Dog");
        Assert.Contains("protected override int Width", dog);
        Assert.DoesNotContain("protected internal override", derived);
        var property = FindProperty(derived, "Dog", "Width");
        Assert.NotNull(property);
        Assert.Contains(property!.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(property.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        Assert.Contains(property.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_CrossAssembly_ProtectedInternalEvent_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
                protected internal virtual event System.EventHandler Changed;
            }
            """);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.DerivedPath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        Assert.Contains("protected override event", GetTypeSection(derived, "Dog"));
        Assert.DoesNotContain("protected internal override", derived);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_CrossAssembly_ProtectedInternalIndexer_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
                protected internal string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.DerivedPath,
            TypeName = "Dog",
            Members = ["this[]"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        var dogIndexer = Assert.Single(FindIndexers(derived, "Dog"));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("protected override", GetTypeSection(derived, "Dog"));
        Assert.DoesNotContain("protected internal override", derived);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_CrossAssembly_ProtectedInternalProperty_ProtectedSetter_OmitsRedundantAccessor()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
                protected internal int Width { get; protected set; }
            }
            """);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.DerivedPath,
            TypeName = "Dog",
            Members = ["Width"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        var dog = GetTypeSection(derived, "Dog");
        Assert.Contains("protected override int Width", dog);
        Assert.DoesNotContain("protected internal override", derived);
        Assert.DoesNotContain("protected set", derived);
        var property = FindProperty(derived, "Dog", "Width");
        Assert.NotNull(property);
        Assert.Contains(property!.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setter = Assert.Single(property.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Empty(setter.Modifiers);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_CrossAssembly_ProtectedInternalPropertySetter_EmitsProtectedSet()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
                public int Width
                {
                    get => 0;
                    protected internal set { }
                }
            }
            """);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.DerivedPath,
            TypeName = "Dog",
            Members = ["Width"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        var property = FindProperty(derived, "Dog", "Width");
        Assert.NotNull(property);
        Assert.Contains(property!.Modifiers, t => t.IsKind(SyntaxKind.PublicKeyword));
        Assert.DoesNotContain(property.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        var setter = Assert.Single(property.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains(setter.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(setter.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        Assert.DoesNotContain("protected internal set", derived);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_CrossAssembly_ProtectedInternalMethod_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
                protected internal virtual void Speak() { }
            }
            """);
        var operation = new PullMembersUpOperation(workspace.Context);
        var beforeLib = await File.ReadAllTextAsync(workspace.LibraryPath);
        var beforeDerived = await File.ReadAllTextAsync(workspace.DerivedPath);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.DerivedPath,
            TypeName = "Dog",
            Members = ["Speak"],
            MakeAbstract = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("protected override void Speak()") &&
            !c.AfterSnippet.Contains("protected internal override"));
        Assert.Contains(result.PendingChanges, c =>
            c.Description != null && c.Description.Contains("Speak"));
        Assert.Equal(beforeLib, await File.ReadAllTextAsync(workspace.LibraryPath));
        Assert.Equal(beforeDerived, await File.ReadAllTextAsync(workspace.DerivedPath));
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static string ExtractTypeBody(string source, string typeName)
    {
        var normalized = NormalizeNewlines(source);
        var start = normalized.IndexOf("class " + typeName, StringComparison.Ordinal);
        if (start < 0)
            start = normalized.IndexOf("interface " + typeName, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Type '{typeName}' not found.");

        var open = normalized.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < normalized.Length; i++)
        {
            if (normalized[i] == '{') depth++;
            else if (normalized[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return normalized.Substring(open, i - open + 1);
            }
        }

        return normalized[open..];
    }

    private static string GetTypeSection(string source, string typeName)
    {
        foreach (var keyword in new[] { "class ", "interface " })
        {
            var marker = keyword + typeName;
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                continue;

            var nextClass = source.IndexOf("class ", start + marker.Length, StringComparison.Ordinal);
            var nextInterface = source.IndexOf("interface ", start + marker.Length, StringComparison.Ordinal);
            var next = nextClass < 0 ? nextInterface
                : nextInterface < 0 ? nextClass
                : Math.Min(nextClass, nextInterface);
            return next < 0 ? source[start..] : source[start..next];
        }

        throw new InvalidOperationException($"Type '{typeName}' not found.");
    }

    private static TypeDeclarationSyntax FindType(string source, string typeName)
    {
        var type = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == typeName);
        Assert.True(type != null, $"Generated source did not contain type '{typeName}':\n{source}");
        return type!;
    }

    private static IReadOnlyList<TypeDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

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

    private static IReadOnlyList<IndexerDeclarationSyntax> FindIndexers(string source, string typeName) =>
        FindType(source, typeName).Members.OfType<IndexerDeclarationSyntax>().ToList();

    private static PropertyDeclarationSyntax? FindProperty(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == name);

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "PullMembersUpCompileTest",
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
        Assert.True(errors.Count == 0, "Generated pull_members_up members did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public string LibraryPath { get; init; } = "";
        public string DerivedPath { get; init; } = "";
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPullMembersUp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
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

        /// <summary>
        /// Lib project referenced by App. <see cref="LibraryPath"/> is the
        /// base type; <see cref="DerivedPath"/> / <see cref="SourcePath"/>
        /// is the derived type that owns the members to pull.
        /// </summary>
        public static async Task<TempWorkspace> CreateReferencedLibraryAsync(string librarySource, string appSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPullMembersUpXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSource = Path.Combine(libDir, "Animal.cs");
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

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                if (context.GetDocumentByPath(libSource) == null || context.GetDocumentByPath(appSourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Lib/App sources.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = solutionPath,
                    SourcePath = appSourcePath,
                    LibraryPath = libSource,
                    DerivedPath = appSourcePath,
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

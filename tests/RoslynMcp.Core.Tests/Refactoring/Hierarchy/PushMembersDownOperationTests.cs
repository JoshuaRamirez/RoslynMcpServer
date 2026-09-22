using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// Operation-level tests for <see cref="PushMembersDownOperation"/>, including optional
/// <c>line</c>, <c>column</c>, <c>targetDerivedTypes</c>, <c>leaveAbstract</c>, and <c>members</c>.
/// </summary>
public class PushMembersDownOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = "",
                TypeName = "Animal",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
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
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
                Members = []
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = "Types.cs",
                TypeName = "Animal",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 optional line disambiguation

    private const string NestedSameNameAnimalSource = """
        namespace TestApp;

        public /* outer-animal */ class Animal
        {
            public string Name { get; set; }

            public /* nested-animal */ class Animal
            {
                public int Age { get; set; }
            }
        }

        public class Dog : Animal
        {
        }

        public class Puppy : Animal.Animal
        {
        }
        """;

    private const string EnumFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* animal-enum */ enum Animal
            {
                Ready
            }
        }

        namespace TestApp
        {
            public /* animal-class */ class Animal
            {
                public string Name { get; set; }
            }

            public class Dog : Animal
            {
            }
        }
        """;

    private const string DelegateFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* animal-delegate */ delegate void Animal();
        }

        namespace TestApp
        {
            public /* animal-class */ class Animal
            {
                public string Name { get; set; }
            }

            public class Dog : Animal
            {
            }
        }
        """;

    private const string EnclosedDerivedAnimalSource = """
        namespace TestApp;

        public /* enclosing-animal */ class Animal
        {
            public string Name { get; set; }

            public /* nested-puppy */ class Puppy : Animal
            {
            }
        }
        """;

    private const string LaterSameNamedAnimalSource = """
        namespace Other
        {
            public /* first-animal */ class Animal
            {
                public string Title { get; set; }
            }

            public class Horse : Animal
            {
            }
        }

        namespace TestApp
        {
            public /* later-animal */ class Animal
            {
                public string Name { get; set; }
            }

            public class Dog : Animal
            {
            }
        }
        """;

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new PushMembersDownParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Animal",
            Members = ["Name"]
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
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
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
                Members = ["Name"],
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
        Assert.Null(FindProperty(updated, "Puppy", "Age"));
        Assert.Null(FindProperty(updated, "Puppy", "Name"));
    }

    [SkippableFact]
    public async Task PushMembersDown_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Age"],
            Line = FindLine(NestedSameNameAnimalSource, "nested-animal")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.Null(FindProperty(updated, "Dog", "Name"));
        Assert.NotNull(FindProperty(updated, "Puppy", "Age"));
    }

    [SkippableFact]
    public async Task PushMembersDown_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"],
            Line = FindLine(NestedSameNameAnimalSource, "outer-animal")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
        Assert.Null(FindProperty(updated, "Puppy", "Age"));
    }

    [SkippableFact]
    public async Task PushMembersDown_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Name"],
                Line = FindLine(EnumFirstThenSameNamedClassSource, "animal-enum")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Dog"));
    }

    [SkippableFact]
    public async Task PushMembersDown_LineOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Name"],
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "animal-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Dog"));
    }

    [SkippableFact]
    public async Task PushMembersDown_SequentialPushes_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(LaterSameNamedAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Title"],
            Line = FindLine(LaterSameNamedAnimalSource, "first-animal")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var second = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"],
            Line = FindLine(afterFirst, "later-animal")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Title"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 1, "Name"));
        Assert.NotNull(FindProperty(updated, "Horse", "Title"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
        Assert.Null(FindProperty(updated, "Horse", "Name"));
        Assert.Null(FindProperty(updated, "Dog", "Title"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 1, "Title"));
    }

    [SkippableFact]
    public async Task PushMembersDown_Line_EnclosedNestedDerived_AddsMemberToNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnclosedDerivedAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"],
            Line = FindLine(EnclosedDerivedAnimalSource, "enclosing-animal")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Null(FindProperty(updated, "Animal", "Name"));
        Assert.NotNull(FindProperty(updated, "Puppy", "Name"));
        Assert.DoesNotContain("abstract", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PushMembersDown_Line_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Age"],
            Line = FindLine(NestedSameNameAnimalSource, "nested-animal"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Age", StringComparison.Ordinal)
            && c.Description.Contains("Puppy", StringComparison.Ordinal));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("Age", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, c =>
            c.Description.Contains("Name", StringComparison.Ordinal)
            && c.Description.Contains("Dog", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameAnimalSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameAnimalSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", FindLine(NestedSameNameAnimalSource, "nested-animal"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Animal");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameAnimalSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", FindLine(NestedSameNameAnimalSource, "outer-animal"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Animal // split-animal
            {
                public string Name { get; set; }

                public class Animal // nested-animal
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-animal");
        Assert.NotEqual(startLine, identifierLine);

        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameAnimalSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_StructFirstPicksStruct()
    {
        const string source = """
            namespace Other
            {
                public struct Animal
                {
                    public int Id;
                }
            }

            namespace TestApp
            {
                public class Animal
                {
                    public string Name { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: null);

        Assert.NotNull(found);
        Assert.IsType<StructDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", FindLine(EnumFirstThenSameNamedClassSource, "animal-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", FindLine(EnumFirstThenSameNamedClassSource, "animal-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", FindLine(DelegateFirstThenSameNamedClassSource, "animal-delegate"));

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
    public async Task PushMembersDown_OmittedLine_EnumFirstThenSameNamedClass_PushesFromClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Single(types);
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
        Assert.Contains("enum Animal", updated, StringComparison.Ordinal);
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedAnimalSource = """
        namespace TestApp;

        public class Animal { public string Name { get; set; } public class Animal { public int Age { get; set; } } }

        public class Dog : Animal
        {
        }

        public class Puppy : Animal.Animal
        {
        }
        """;

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new PushMembersDownParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Animal",
            Members = ["Name"]
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
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
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
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
            PushMembersDownOperation.Validate(new PushMembersDownParams
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
    public async Task PushMembersDown_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
        Assert.Null(FindProperty(updated, "Puppy", "Age"));
        Assert.Null(FindProperty(updated, "Puppy", "Name"));
    }

    [SkippableFact]
    public async Task PushMembersDown_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Age"],
            Line = FindLine(NestedSameNameAnimalSource, "nested-animal")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.Null(FindProperty(updated, "Dog", "Name"));
        Assert.NotNull(FindProperty(updated, "Puppy", "Age"));
    }

    [SkippableFact]
    public async Task PushMembersDown_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var line = FindLine(SameLineNestedAnimalSource, "public class Animal { public string Name");
        var column = ColumnOf(SameLineNestedAnimalSource, "Animal { public int Age");

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Age"],
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.NotNull(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.Null(FindProperty(updated, "Dog", "Name"));
        Assert.NotNull(FindProperty(updated, "Puppy", "Age"));
    }

    [SkippableFact]
    public async Task PushMembersDown_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var line = FindLine(SameLineNestedAnimalSource, "public class Animal { public string Name");
        var column = ColumnOf(SameLineNestedAnimalSource, "Animal { public string Name");

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"],
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.NotNull(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
        Assert.Null(FindProperty(updated, "Puppy", "Age"));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameAnimalSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: null, column: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedAnimalSource).GetRoot();
        var line = FindLine(SameLineNestedAnimalSource, "public class Animal { public string Name");
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", line, ColumnOf(SameLineNestedAnimalSource, "Animal { public int Age"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Animal");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedAnimalSource).GetRoot();
        var line = FindLine(SameLineNestedAnimalSource, "public class Animal { public string Name");
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", line, ColumnOf(SameLineNestedAnimalSource, "Animal { public string Name"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedAnimalSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedAnimalSource, "Animal { public int Age");
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var enumColumn = ColumnOf(EnumFirstThenSameNamedClassSource, "Animal");
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", line: null, enumColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var delegateColumn = ColumnOf(DelegateFirstThenSameNamedClassSource, "Animal()");
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", line: null, delegateColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_StructFirstPicksStruct()
    {
        const string source = """
            namespace Other
            {
                public struct Animal
                {
                    public int Id;
                }
            }

            namespace TestApp
            {
                public class Animal
                {
                    public string Name { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var structColumn = ColumnOf(source, "Animal");
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", line: null, structColumn);

        Assert.NotNull(found);
        Assert.IsType<StructDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Animal // split-animal
            {
                public string Name { get; set; }

                public class Animal // nested-animal
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-animal");
        Assert.NotEqual(startLine, identifierLine);

        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", identifierLine, ColumnOf(source, "Animal // split-animal"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task PushMembersDown_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Animal // split-animal
            {
                public string Name { get; set; }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Animal");
        var identifierLine = FindLine(source, "split-animal");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"],
            Line = identifierLine,
            Column = ColumnOf(source, "Animal // split-animal")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Null(FindProperty(updated, "Animal", "Name"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnEnumIdentifier_PicksEnum()
    {
        const string source = """
            namespace TestApp { public enum Animal { Ready } public class Animal { public string Name { get; set; } }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var line = FindLine(source, "public enum Animal");
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal", line, ColumnOf(source, "Animal { Ready }"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task PushMembersDown_ColumnOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        const string source = """
            namespace TestApp { public enum Animal { Ready } public class Animal { public string Name { get; set; } } public class Dog : Animal { } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Name"],
                Line = FindLine(source, "public enum Animal"),
                Column = ColumnOf(source, "Animal { Ready }")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Dog"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(
            root, "Animal",
            FindLine(DelegateFirstThenSameNamedClassSource, "animal-delegate"),
            ColumnOf(DelegateFirstThenSameNamedClassSource, "Animal()"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task PushMembersDown_ColumnOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Name"],
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "animal-delegate"),
                Column = ColumnOf(DelegateFirstThenSameNamedClassSource, "Animal()")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("Name", ExtractTypeBody(updated, "Dog"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameAnimalSource).GetRoot();
        var found = PushMembersDownOperation.FindTypeDeclaration(root, "Animal", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task PushMembersDown_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Name"],
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PushMembersDown_ColumnAndLine_UnknownTypeName_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameAnimalSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
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
    public async Task PushMembersDown_Column_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedAnimalSource, "public class Animal { public string Name");

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Age"],
            Line = line,
            Column = ColumnOf(SameLineNestedAnimalSource, "Animal { public int Age"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Age", StringComparison.Ordinal)
            && c.Description.Contains("Puppy", StringComparison.Ordinal));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("Age", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, c =>
            c.Description.Contains("Name", StringComparison.Ordinal)
            && c.Description.Contains("Dog", StringComparison.Ordinal));
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
    public async Task PushMembersDown_SequentialColumn_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedAnimalSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var line = FindLine(SameLineNestedAnimalSource, "public class Animal { public string Name");

        var first = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"],
            Line = line,
            Column = ColumnOf(SameLineNestedAnimalSource, "Animal { public string Name")
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Age"],
            Line = FindLine(afterFirst, "Animal { public int Age"),
            Column = ColumnOf(afterFirst, "Animal { public int Age")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Animal");
        Assert.Equal(2, types.Count);
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 1, "Age"));
        Assert.NotNull(FindProperty(updated, "Dog", "Name"));
        Assert.NotNull(FindProperty(updated, "Puppy", "Age"));
        Assert.Null(FindProperty(updated, "Dog", "Age"));
        Assert.Null(FindProperty(updated, "Puppy", "Name"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 0, "Age"));
        Assert.Null(FindPropertyOnNthType(updated, "Animal", 1, "Name"));
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task PushMembersDown_MethodToAllDerived_MovesMember()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public class Cat : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"]
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        var cat = ExtractTypeBody(text, "Cat");
        Assert.DoesNotContain("Speak", animal);
        Assert.Contains("Speak", dog);
        Assert.Contains("return 1", dog);
        Assert.Contains("Speak", cat);
        Assert.Contains("return 1", cat);
        Assert.DoesNotContain("virtual", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_PropertyToAllDerived_MovesProperty()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name { get; set; } = "";
            }

            public class Dog : Animal
            {
            }

            public class Cat : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Name", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Name", ExtractTypeBody(text, "Dog"));
        Assert.Contains("Name", ExtractTypeBody(text, "Cat"));
    }

    [SkippableFact]
    public async Task PushMembersDown_MultipleMembers_MovesAll()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name { get; set; } = "";

                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name", "Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.DoesNotContain("Name", animal);
        Assert.DoesNotContain("Speak", animal);
        Assert.Contains("Name", dog);
        Assert.Contains("Speak", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_NamedSubset_PushesOnlySpecifiedDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public class Cat : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"],
            TargetDerivedTypes = ["Dog"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Cat"));
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_KeepsAbstractOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
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
    public async Task PushMembersDown_DefaultInterfaceMethod_CopiesToImplementers()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Speak()
                {
                    return 1;
                }
            }

            public class Dog : IAnimal
            {
            }

            public class Cat : IAnimal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "IAnimal",
            Members = ["Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Speak", ExtractTypeBody(text, "IAnimal"));
        Assert.Contains("public", ExtractTypeBody(text, "Dog"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Dog"));
        Assert.Contains("public", ExtractTypeBody(text, "Cat"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Cat"));
    }

    [SkippableFact]
    public async Task PushMembersDown_GenericBase_SubstitutesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public class Box<T>
            {
                public T GetValue()
                {
                    return default;
                }
            }

            public class StringBox : Box<string>
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            Members = ["GetValue"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var box = ExtractTypeBody(text, "Box");
        var stringBox = ExtractTypeBody(text, "StringBox");
        Assert.DoesNotContain("GetValue", box);
        Assert.Contains("string GetValue()", stringBox);
        Assert.DoesNotContain("T GetValue()", stringBox);
    }

    [SkippableFact]
    public async Task PushMembersDown_VirtualMember_KeepsVirtualForFurtherOverrides()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public class Puppy : Dog
            {
                public override int Speak()
                {
                    return 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var dog = ExtractTypeBody(text, "Dog");
        var puppy = ExtractTypeBody(text, "Puppy");
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Animal"));
        Assert.Contains("virtual", dog);
        Assert.Contains("Speak", dog);
        Assert.Contains("override", puppy);
        Assert.Contains("Speak", puppy);
    }

    [SkippableFact]
    public async Task PushMembersDown_DefaultInterfaceMethod_KeepsBodyOnDerivedInterface()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Speak()
                {
                    return 1;
                }
            }

            public interface IDog : IAnimal
            {
            }

            public class Cat : IDog
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "IAnimal",
            Members = ["Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var derived = ExtractTypeBody(text, "IDog");
        Assert.Contains("Speak", derived);
        Assert.Contains("return 1", derived);
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Cat"));
    }

    [SkippableFact]
    public async Task PushMembersDown_OneVariableFromMultiField_LeavesSibling()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Selected, Untouched;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Selected"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.DoesNotContain("Selected", animal);
        Assert.Contains("Untouched", animal);
        Assert.Contains("Selected", dog);
        Assert.DoesNotContain("Untouched", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_BothVariablesFromMultiField_CopiesEachOnce()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Selected, Untouched;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Selected", "Untouched"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.DoesNotContain("Selected", animal);
        Assert.DoesNotContain("Untouched", animal);
        Assert.Equal(1, CountOccurrences(dog, "Selected"));
        Assert.Equal(1, CountOccurrences(dog, "Untouched"));
    }

    [SkippableFact]
    public async Task PushMembersDown_FieldLikeEvent_MovesEventOntoDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.DoesNotContain("event System.EventHandler Changed", animal);
        Assert.Contains("public event System.EventHandler Changed", dog);
        Assert.DoesNotContain("override", dog);
        Assert.DoesNotContain("abstract", text);
    }

    [SkippableFact]
    public async Task PushMembersDown_AccessorStyleEvent_MovesEventOntoDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.DoesNotContain("event System.EventHandler Changed", animal);
        Assert.Contains("public event System.EventHandler Changed", dog);
        Assert.Contains("add", dog);
        Assert.Contains("remove", dog);
        Assert.DoesNotContain("override", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_MultiVariableEventField_LeavesUnrelatedDeclarator()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed, Other;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public event System.EventHandler Other", animal);
        Assert.DoesNotContain("Changed", animal);
        Assert.DoesNotContain("Changed, Other", text);
        Assert.Contains("public event System.EventHandler Changed", dog);
        Assert.DoesNotContain("Other", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_FieldLikeEventKeepsAbstractOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_LeaveAbstract_AccessorStyleEventKeepsAbstractOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_LeaveAbstract_MultiVariableEventField_LeavesUnrelatedAndOverridesSelected()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed, Other;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract event System.EventHandler Changed", animal);
        Assert.Contains("public event System.EventHandler Other", animal);
        Assert.DoesNotContain("Changed, Other", text);
        Assert.Contains("override event System.EventHandler Changed", dog);
        Assert.DoesNotContain("Other", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_OverrideEvent_PreservesAbstractOverride()
    {
        const string source = """
            namespace TestApp;

            public abstract class Creature
            {
                public abstract event System.EventHandler Changed;
            }

            public class Animal : Creature
            {
                public override event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract override event System.EventHandler Changed", animal);
        Assert.Contains("override event System.EventHandler Changed", dog);
        Assert.DoesNotContain("new ", animal);
    }

    [SkippableFact]
    public async Task PushMembersDown_Event_Preview_WritesNothing_AndDescribesEvent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
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
    public async Task PushMembersDown_Event_LeavesMethodsPropertiesAndFieldsOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name { get; set; } = "";
                public int Age;
                public event System.EventHandler Changed;

                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public string Name { get; set; }", animal);
        Assert.Contains("public int Age", animal);
        Assert.Contains("public int Speak()", animal);
        Assert.DoesNotContain("event System.EventHandler Changed", animal);
        Assert.Contains("public event System.EventHandler Changed", dog);
        Assert.DoesNotContain("Name", dog);
        Assert.DoesNotContain("Age", dog);
        Assert.DoesNotContain("Speak", dog);
    }

    #endregion

    #region Indexers

    private const string MixedIndexerSource = """
        namespace TestApp;

        public class Animal
        {
            public int Count { get; set; }

            public string this[int i]
            {
                get => "";
                set { }
            }

            public void Work() { }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_Default_PublicIndexer_MovesIndexerOntoDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Empty(FindIndexers(updated, "Animal"));
        Assert.Contains("public int Count { get; set; }", GetTypeSection(updated, "Animal"));
        Assert.Contains("public void Work()", GetTypeSection(updated, "Animal"));
        Assert.DoesNotContain(
            FindType(updated, "Dog").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task PushMembersDown_MembersFilter_IndexerAliases_MovesOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = [memberName]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Dog"));
        Assert.Empty(FindIndexers(updated, "Animal"));
        Assert.Contains("public int Count { get; set; }", GetTypeSection(updated, "Animal"));
        Assert.Contains("public void Work()", GetTypeSection(updated, "Animal"));
        Assert.DoesNotContain("Count", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("Work", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_OrdinaryProperty_LeavesIndexerOnSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Count"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "Dog", "Count");
        Assert.NotNull(property);
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Empty(FindIndexers(updated, "Dog"));
        Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Animal"));
        Assert.DoesNotContain("public int Count", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_IndexerLeavesOverrideOnDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var animalIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains(animalIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.DoesNotContain(animalIndexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Null(animalIndexer.ExpressionBody);
        Assert.All(animalIndexer.AccessorList!.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("abstract class", updated);
        Assert.DoesNotContain("abstractclass", updated);
        Assert.Contains("this[int i]", GetTypeSection(updated, "Animal"));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_IndexerToDerivedInterface_EmitsSignature()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                string this[int i] { get; set; }
            }

            public interface IDog : IAnimal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "IAnimal",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var sourceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "IDog"));
        Assert.Empty(derivedIndexer.Modifiers);
        Assert.Null(derivedIndexer.ExpressionBody);
        Assert.Contains(derivedIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(derivedIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.All(derivedIndexer.AccessorList.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains("this[int i]", GetTypeSection(updated, "IDog"));
        Assert.Contains(sourceIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(
            FindType(updated, "IDog").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_IndexerToDerivedInterface_PrivateSetter_EmitsGetOnly()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int this[int i] { get => i; private set { } }
            }

            public interface IDog : IAnimal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "IAnimal",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "IDog"));
        var sourceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        Assert.Contains(derivedIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(derivedIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.DoesNotContain(derivedIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.Contains(sourceIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(sourceIndexer.AccessorList.Accessors, a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration)
            && a.Modifiers.Any(SyntaxKind.PrivateKeyword));
        Assert.Contains("this[int i]", GetTypeSection(updated, "IDog"));
    }

    [SkippableFact]
    public async Task PushMembersDown_Indexer_Preview_WritesNothing_AndDescribesIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
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
        Assert.DoesNotContain(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Replace("this[int i]", "", StringComparison.Ordinal).Contains("this[]"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PushMembersDown_GetOnlyIndexer_PreservesGetOnly()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int this[int i] => i;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.True(
            indexer.ExpressionBody != null
            || (indexer.AccessorList != null
                && indexer.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))
                && indexer.AccessorList.Accessors.All(a => !a.IsKind(SyntaxKind.SetAccessorDeclaration))));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Dog"));
        Assert.Empty(FindIndexers(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_Indexer_RefKindParameter_Preserved()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int this[in int i] => i;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Equal("in", Assert.Single(indexer.ParameterList.Parameters).Modifiers.ToString().Trim());
        Assert.Contains("this[in int i]", updated);
        Assert.Empty(FindIndexers(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_Indexer_LeavesMethodsPropertiesFieldsAndEventsOnSource()
    {
        const string source = """
            namespace TestApp;

            public class Animal
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

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var animal = GetTypeSection(updated, "Animal");
        var dog = GetTypeSection(updated, "Dog");
        Assert.Contains("public string Name { get; set; }", animal);
        Assert.Contains("public int Age", animal);
        Assert.Contains("event System.EventHandler Changed", animal);
        Assert.Contains("public int Speak()", animal);
        Assert.Empty(FindIndexers(updated, "Animal"));
        Assert.Single(FindIndexers(updated, "Dog"));
        Assert.DoesNotContain("Name", dog);
        Assert.DoesNotContain("Age", dog);
        Assert.DoesNotContain("Changed", dog);
        Assert.DoesNotContain("Speak", dog);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_SpecificIndexerDisplay_LeavesOtherIndexerOnSource()
    {
        const string source = """
            namespace TestApp;

            public class Animal
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

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[int i]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var sourceIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Equal("string", Assert.Single(sourceIndexer.ParameterList.Parameters).Type!.ToString());
        Assert.Equal("int", Assert.Single(derivedIndexer.ParameterList.Parameters).Type!.ToString());
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_AbstractIndexer_MaterializesAccessorBodiesOnDerived()
    {
        const string source = """
            namespace TestApp;

            public abstract class Animal
            {
                public abstract string this[int i] { get; set; }
            }

            public abstract class Bird : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(FindIndexers(updated, "Animal"));
        var birdIndexer = Assert.Single(FindIndexers(updated, "Bird"));
        Assert.Contains(birdIndexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.DoesNotContain(birdIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.All(birdIndexer.AccessorList!.Accessors, a => Assert.NotNull(a.Body));
        Assert.Contains("NotImplementedException", GetTypeSection(updated, "Bird"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_AbstractIndexer_MaterializesOverrideBodies()
    {
        const string source = """
            namespace TestApp;

            public abstract class Animal
            {
                public abstract string this[int i] { get; set; }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var animalIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains(animalIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.All(animalIndexer.AccessorList!.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.All(dogIndexer.AccessorList!.Accessors, a => Assert.NotNull(a.Body));
        Assert.Contains("NotImplementedException", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_GenericIndexer_ConflictsWithSubstitutedSignature()
    {
        const string source = """
            namespace TestApp;

            public class Box<T>
            {
                public T this[T key] => key;
            }

            public class IntBox : Box<int>
            {
                public int this[int key] => key;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Box",
                Members = ["this[]"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PushMembersDown_GenericIndexer_DifferentSubstitutedArity_DoesNotConflict()
    {
        const string source = """
            namespace TestApp;

            public class Box<T>
            {
                public T this[T key] => key;
            }

            public class StringBox : Box<string>
            {
                public int this[int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(FindIndexers(updated, "Box"));
        var derived = FindIndexers(updated, "StringBox");
        Assert.Equal(2, derived.Count);
        Assert.Contains(derived, indexer =>
            Assert.Single(indexer.ParameterList.Parameters).Type!.ToString() == "string");
        Assert.Contains(derived, indexer =>
            Assert.Single(indexer.ParameterList.Parameters).Type!.ToString() == "int");
        AssertCompiles(updated);
    }

    [Fact]
    public void ReduceCrossAssemblyOverrideAccessibility_DifferentAssembly_StripsInternal()
    {
        var (indexer, sameAssemblyType, otherAssemblyType) = CompileProtectedInternalIndexerPair();
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
            SyntaxFactory.Token(SyntaxKind.InternalKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword));

        var reduced = PushMembersDownOperation.ReduceCrossAssemblyOverrideAccessibility(
            modifiers, indexer, otherAssemblyType);

        Assert.Contains(reduced, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(reduced, t => t.IsKind(SyntaxKind.InternalKeyword));
        Assert.Contains(reduced, t => t.IsKind(SyntaxKind.OverrideKeyword));

        var same = PushMembersDownOperation.ReduceCrossAssemblyOverrideAccessibility(
            modifiers, indexer, sameAssemblyType);
        Assert.Contains(same, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.Contains(same, t => t.IsKind(SyntaxKind.InternalKeyword));
    }

    [Fact]
    public void ReduceIndexerOverrideAccessibility_CrossAssembly_ProtectedInternalSetter_BecomesProtected()
    {
        var (indexerSymbol, _, otherAssemblyType, indexerSyntax) = CompilePublicIndexerWithProtectedInternalSetter();

        var reduced = PushMembersDownOperation.ReduceIndexerOverrideAccessibility(
            indexerSyntax, indexerSymbol, otherAssemblyType);

        Assert.Contains(reduced.Modifiers, t => t.IsKind(SyntaxKind.PublicKeyword));
        Assert.DoesNotContain(reduced.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        var setter = Assert.Single(reduced.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains(setter.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(setter.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_SameAssembly_KeepsProtectedInternalIndexer()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected internal string this[int i]
                {
                    get => "";
                    set { }
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["this[]"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalIndexer_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                protected internal string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["this[]"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalSetter_EmitsProtectedSet()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                public string this[int i]
                {
                    get => "";
                    protected internal set { }
                }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["this[]"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        var dogIndexer = Assert.Single(FindIndexers(derived, "Dog"));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.PublicKeyword));
        Assert.DoesNotContain(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        var setter = Assert.Single(dogIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains(setter.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(setter.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        Assert.DoesNotContain("protected internal set", derived);
    }

    #endregion

    #region CS0507 methods / properties / events

    [Fact]
    public void ReduceOverrideAccessibility_CrossAssembly_ProtectedInternalPropertySetter_BecomesProtected()
    {
        var (propertySymbol, _, otherAssemblyType, propertySyntax) = CompilePublicPropertyWithProtectedInternalSetter();

        var reduced = PushMembersDownOperation.ReduceOverrideAccessibility(
            propertySyntax, propertySymbol, otherAssemblyType);

        Assert.Contains(reduced.Modifiers, t => t.IsKind(SyntaxKind.PublicKeyword));
        Assert.DoesNotContain(reduced.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
        var setter = Assert.Single(reduced.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains(setter.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(setter.Modifiers, t => t.IsKind(SyntaxKind.InternalKeyword));
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_SameAssembly_KeepsProtectedInternalMethod()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected internal virtual void Speak() { }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected internal override void Speak()", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("protected override void Speak()", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_SameAssembly_KeepsProtectedInternalProperty()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected internal int Width { get; set; }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Width"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var dog = GetTypeSection(updated, "Dog");
        Assert.Contains("protected internal override int Width", dog);
        Assert.DoesNotContain("protected override int Width", dog);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_SameAssembly_KeepsProtectedInternalEvent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected internal virtual event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Changed"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var dog = GetTypeSection(updated, "Dog");
        Assert.Contains("protected internal override event", dog);
        Assert.DoesNotContain("protected override event", dog);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalMethod_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                protected internal virtual void Speak() { }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["Speak"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        Assert.Contains("protected override void Speak()", GetTypeSection(derived, "Dog"));
        Assert.DoesNotContain("protected internal override", derived);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalProperty_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                protected internal int Width { get; set; }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["Width"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalEvent_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                protected internal virtual event System.EventHandler Changed;
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["Changed"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var derived = NormalizeNewlines(await File.ReadAllTextAsync(workspace.DerivedPath));
        Assert.Contains("protected override event", GetTypeSection(derived, "Dog"));
        Assert.DoesNotContain("protected internal override", derived);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_SameAssembly_ProtectedInternalProperty_ProtectedSetter_KeepsBoth()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected internal int Width { get; protected set; }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Width"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var dog = GetTypeSection(updated, "Dog");
        Assert.Contains("protected internal override int Width", dog);
        Assert.Contains("protected set", dog);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalProperty_ProtectedSetter_OmitsRedundantAccessor()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                protected internal int Width { get; protected set; }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["Width"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalPropertySetter_EmitsProtectedSet()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                public int Width
                {
                    get => 0;
                    protected internal set { }
                }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["Width"],
            LeaveAbstract = true
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
    public async Task PushMembersDown_LeaveAbstract_CrossAssembly_ProtectedInternalMethod_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                protected internal virtual void Speak() { }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new PushMembersDownOperation(workspace.Context);
        var beforeLib = await File.ReadAllTextAsync(workspace.LibraryPath);
        var beforeDerived = await File.ReadAllTextAsync(workspace.DerivedPath);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.LibraryPath,
            TypeName = "Animal",
            Members = ["Speak"],
            LeaveAbstract = true,
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

    #region P0 Rejects

    [SkippableFact]
    public async Task PushMembersDown_NoDerived_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.DerivedClassesNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_MissingNamedDerived_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"],
                TargetDerivedTypes = ["Bird"]
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_NameConflict_Throws()
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
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_SignatureConflict_Throws()
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
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_InterfaceImplementation_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Speak();
            }

            public class Animal : IAnimal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.MemberRequiredByContract, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_StaticFieldToDerivedInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                public static int Count = 1;
            }

            public interface IDog : IAnimal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "IAnimal",
                Members = ["Count"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_ExternalDerived_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var compilation = await workspace.Context.Solution.Projects.First().GetCompilationAsync();
        Assert.NotNull(compilation);
        var exception = compilation.GetTypeByMetadataName("System.Exception");
        Assert.NotNull(exception);

        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.ValidateDerivedIsEditable(exception));

        Assert.Equal(ErrorCodes.DerivedClassNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_MemberNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Missing"]
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_ReferencedThroughBaseType_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Uses
            {
                public static int Call(Animal animal)
                {
                    return animal.Speak();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.MemberRequiredByContract, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_OverrideRequiredByAbstractBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public abstract class Creature
            {
                public abstract int Speak();
            }

            public class Animal : Creature
            {
                public override int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.MemberRequiredByContract, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstractOnStatic_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public static int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"],
                LeaveAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_StaticEvent_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public static event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Changed"],
                LeaveAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_ExplicitInterfaceEvent_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface INotify
            {
                event System.EventHandler Changed;
            }

            public class Animal : INotify
            {
                event System.EventHandler INotify.Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Changed"],
                LeaveAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_EventRaisedInSource_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed;

                public void Speak()
                {
                    Changed?.Invoke(this, System.EventArgs.Empty);
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Changed"],
                LeaveAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_NamedSubsetOmitsConcreteDerived_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }

            public class Cat : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Changed"],
                TargetDerivedTypes = ["Dog"],
                LeaveAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_Indexer_UnknownName_ThrowsMemberNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["this[string key]"]
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_PrivateSetter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int this[int i] { get => i; private set { } }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["this[]"],
                LeaveAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_ExplicitInterfaceIndexer_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public class Animal : ILookup
            {
                string ILookup.this[int i] => "";
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["this[int i]"],
                LeaveAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region allFiles

    private const string EligiblePushFileA = """
        namespace TestApp;

        public class AnimalA
        {
            public int Speak()
            {
                return 1;
            }
        }

        public class DogA : AnimalA
        {
        }
        """;

    private const string EligiblePushFileB = """
        namespace TestApp;

        public class AnimalB
        {
            public string Name { get; set; }
        }

        public class DogB : AnimalB
        {
        }
        """;

    private const string IneligibleNoDerivedFile = """
        namespace TestApp;

        public class Standalone
        {
            public int Speak()
            {
                return 1;
            }
        }
        """;

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrTypeName_DoesNotThrow()
    {
        PushMembersDownOperation.Validate(new PushMembersDownParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                AllFiles = true,
                TypeName = "AnimalA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMembers_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                AllFiles = true,
                Members = new[] { "Speak" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTargetDerivedTypes_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                AllFiles = true,
                TargetDerivedTypes = new[] { "DogA" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithEmptyTargetDerivedTypes_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                AllFiles = true,
                TargetDerivedTypes = Array.Empty<string>()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void MemberCascadeKey_DistinguishesMethodOverloads_OmitsContainingType()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Root
            {
                public void M(string s) { }
                public void M(int i) { }
                public int P { get; set; }
            }

            class Middle
            {
                public void M(string s) { }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "CascadeKeyTest",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var prop = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();
        var rootMString = model.GetDeclaredSymbol(methods[0])!;
        var rootMInt = model.GetDeclaredSymbol(methods[1])!;
        var middleMString = model.GetDeclaredSymbol(methods[2])!;
        var p = model.GetDeclaredSymbol(prop)!;

        var keyRootString = PushMembersDownOperation.MemberCascadeKey(rootMString);
        var keyRootInt = PushMembersDownOperation.MemberCascadeKey(rootMInt);
        var keyMiddleString = PushMembersDownOperation.MemberCascadeKey(middleMString);
        Assert.NotEqual(keyRootString, keyRootInt);
        Assert.Equal(keyRootString, keyMiddleString);
        Assert.Equal("P", PushMembersDownOperation.MemberCascadeKey(p));
    }

    [Fact]
    public void MemberCascadeKeyForTarget_SubstitutesConstructedGenericSignature()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Root<T>
            {
                public int M(T value) { return 1; }
            }

            class Middle : Root<int>
            {
            }
            """);
        var compilation = CSharpCompilation.Create(
            "CascadeKeyGenericTest",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = compilation.GetSemanticModel(tree);
        var rootType = model.GetDeclaredSymbol(
            tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .First(c => c.Identifier.Text == "Root"))!;
        var middleType = model.GetDeclaredSymbol(
            tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .First(c => c.Identifier.Text == "Middle"))!;
        var method = rootType.GetMembers("M").OfType<IMethodSymbol>().Single();

        var rawKey = PushMembersDownOperation.MemberCascadeKey(method);
        var targetKey = PushMembersDownOperation.MemberCascadeKeyForTarget(
            method, rootType, middleType);

        Assert.Contains("T", rawKey, StringComparison.Ordinal);
        Assert.DoesNotContain("T", targetKey, StringComparison.Ordinal);
        Assert.Contains("Int32", targetKey, StringComparison.Ordinal);
        Assert.NotEqual(rawKey, targetKey);
    }


    [Fact]
    public void Validate_AllFilesTrue_RelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
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
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                AllFiles = false,
                TypeName = "AnimalA",
                Members = new[] { "Speak" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Push members down", PushMembersDownOperation.BuildAllFilesDescription(1));
        Assert.Equal("Push members down from 2 types", PushMembersDownOperation.BuildAllFilesDescription(2));
    }

    [SkippableFact]
    public async Task PushMembersDown_OmittedAllFiles_KeepsSingleSitePush()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligiblePushFileA);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "AnimalA",
            Members = new[] { "Speak" }
        });

        Assert.True(result.Success);
        var text = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "AnimalA"));
        Assert.Contains("Speak", ExtractTypeBody(text, "DogA"));
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_PushesEligibleTypesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligiblePushFileA),
            ("FileB.cs", EligiblePushFileB),
            ("FileC.cs", IneligibleNoDerivedFile));
        var operation = new PushMembersDownOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain("Speak", ExtractTypeBody(updatedA, "AnimalA"));
        Assert.Contains("Speak", ExtractTypeBody(updatedA, "DogA"));
        Assert.DoesNotContain("Name", ExtractTypeBody(updatedB, "AnimalB"));
        Assert.Contains("Name", ExtractTypeBody(updatedB, "DogB"));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_WithoutSourceFileOrTypeName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligiblePushFileA),
            ("FileB.cs", EligiblePushFileB));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 1);
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligiblePushFileA);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                AllFiles = false,
                TypeName = "AnimalA",
                Members = new[] { "Speak" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligiblePushFileA);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                AllFiles = true,
                TypeName = "AnimalA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_WithMembers_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligiblePushFileA);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                AllFiles = true,
                Members = new[] { "Speak" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligiblePushFileA),
            ("FileB.cs", EligiblePushFileB),
            ("FileC.cs", IneligibleNoDerivedFile));
        var operation = new PushMembersDownOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges!.Count >= 1);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleNoDerivedFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Empty(result.Changes.FilesCreated);
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligiblePushFileA),
            ("FileB.cs", EligiblePushFileB));
        var operation = new PushMembersDownOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
    }

    private const string CascadeRootFile = """
        namespace TestApp;

        public class Root
        {
            public int Cascaded()
            {
                return 1;
            }
        }

        public class Middle : Root
        {
        }
        """;

    private const string CascadeLeafFile = """
        namespace TestApp;

        public class Leaf : Middle
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_DoesNotCascadeThroughMiddleDerived()
    {
        // Leaf.cs sorts before Root.cs alphabetically? "CascadeLeaf" vs - use names
        // Leaf.cs before MiddleRoot.cs so Leaf is visited first (no members), then Root
        // pushes Cascaded onto Middle. Without cascade tracking, Middle would then
        // push Cascaded onto Leaf.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Leaf.cs", CascadeLeafFile),
            ("Root.cs", CascadeRootFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var root = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        var leaf = await File.ReadAllTextAsync(workspace.SourcePaths["Leaf.cs"]);
        Assert.DoesNotContain("Cascaded", ExtractTypeBody(root, "Root"));
        Assert.Contains("Cascaded", ExtractTypeBody(root, "Middle"));
        Assert.DoesNotContain("Cascaded", ExtractTypeBody(leaf, "Leaf"));
    }

    private const string PartialBasePartA = """
        namespace TestApp;

        public partial class Animal
        {
            public int Speak()
            {
                return 1;
            }
        }

        public class Dog : Animal
        {
        }
        """;

    private const string PartialBasePartB = """
        namespace TestApp;

        public partial class Animal
        {
            public string Name { get; set; }
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_PushesMembersFromEveryPartialDeclaration()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("AnimalA.cs", PartialBasePartA),
            ("AnimalB.cs", PartialBasePartB));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var animalA = await File.ReadAllTextAsync(workspace.SourcePaths["AnimalA.cs"]);
        var animalB = await File.ReadAllTextAsync(workspace.SourcePaths["AnimalB.cs"]);
        Assert.DoesNotContain("Speak", ExtractTypeBody(animalA, "Animal"));
        Assert.Contains("Speak", ExtractTypeBody(animalA, "Dog"));
        Assert.DoesNotContain("Name", ExtractTypeBody(animalB, "Animal"));
        Assert.Contains("Name", ExtractTypeBody(animalA, "Dog"));
    }


    private const string SameFilePartialsFile = """
        namespace TestApp;

        public partial class Animal
        {
            public int Speak()
            {
                return 1;
            }
        }

        public partial class Animal
        {
            public string Name { get; set; }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_PushesMembersFromSameFilePartials()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", SameFilePartialsFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Name", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Dog"));
        Assert.Contains("Name", ExtractTypeBody(text, "Dog"));
    }

    private const string OverloadCascadeRootFile = """
        namespace TestApp;

        public class Root
        {
            public int M(string s)
            {
                return 1;
            }
        }

        public class Middle : Root
        {
            public int M(int i)
            {
                return 2;
            }
        }
        """;

    private const string OverloadCascadeLeafFile = """
        namespace TestApp;

        public class Leaf : Middle
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_CascadeKeepsUnrelatedOverloads()
    {
        // Root.M(string) is pushed onto Middle; Middle.M(int) must still reach Leaf.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Leaf.cs", OverloadCascadeLeafFile),
            ("Root.cs", OverloadCascadeRootFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var root = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        var leaf = await File.ReadAllTextAsync(workspace.SourcePaths["Leaf.cs"]);
        Assert.DoesNotContain("M(string", ExtractTypeBody(root, "Root"));
        Assert.Contains("M(string", ExtractTypeBody(root, "Middle"));
        Assert.DoesNotContain("M(int", ExtractTypeBody(root, "Middle"));
        Assert.Contains("M(int", ExtractTypeBody(leaf, "Leaf"));
        Assert.DoesNotContain("M(string", ExtractTypeBody(leaf, "Leaf"));
    }

    private const string PartialDependentFieldPartA = """
        namespace TestApp;

        public partial class Animal
        {
            private int _x;
        }

        public class Dog : Animal
        {
        }
        """;

    private const string PartialDependentFieldPartB = """
        namespace TestApp;

        public partial class Animal
        {
            public int Speak()
            {
                return _x;
            }
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RevisitsPartialAfterDependentMemberMoves()
    {
        // Field file sorts first; validation initially rejects moving _x while Speak
        // still references it. After Speak moves from the later part, revisit must
        // push _x too — otherwise Dog.Speak references a private field still on Animal.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("AnimalA.cs", PartialDependentFieldPartA),
            ("AnimalB.cs", PartialDependentFieldPartB));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var animalA = await File.ReadAllTextAsync(workspace.SourcePaths["AnimalA.cs"]);
        var animalB = await File.ReadAllTextAsync(workspace.SourcePaths["AnimalB.cs"]);
        Assert.DoesNotContain("_x", ExtractTypeBody(animalA, "Animal"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(animalB, "Animal"));
        Assert.Contains("_x", ExtractTypeBody(animalA, "Dog"));
        Assert.Contains("Speak", ExtractTypeBody(animalA, "Dog"));
    }

    private const string GenericCascadeRootFile = """
        namespace TestApp;

        public class Root<T>
        {
            public int M(T value)
            {
                return 1;
            }
        }

        public class Middle : Root<int>
        {
        }
        """;

    private const string GenericCascadeLeafFile = """
        namespace TestApp;

        public class Leaf : Middle
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_CascadeUsesConstructedGenericSignature()
    {
        // Root<T>.M(T) pushes onto Middle : Root<int> as M(int). Cascade tracking
        // must record M(int), not M(T), or Middle would re-push to Leaf.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Leaf.cs", GenericCascadeLeafFile),
            ("Root.cs", GenericCascadeRootFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var root = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        var leaf = await File.ReadAllTextAsync(workspace.SourcePaths["Leaf.cs"]);
        Assert.DoesNotContain("M(", ExtractTypeBody(root, "Root"));
        Assert.Contains("M(int", ExtractTypeBody(root, "Middle"));
        Assert.DoesNotContain("M(", ExtractTypeBody(leaf, "Leaf"));
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_SourceFileFilter_SkipsLinkedMultiViewDerived()
    {
        // Base lives only in ProjectA; derived is linked into A+B. sourceFile limits
        // the walk to Base.cs — linked-path counts must still see Derived's multi-view
        // and skip, rather than pushing and coalescing over divergent siblings.
        const string baseSource = """
            namespace TestApp;

            public class Root
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;
        const string derivedSource = """
            namespace TestApp;

            public class Dog : Root
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedDerivedAsync(
            baseSource, derivedSource);
        var beforeBase = await File.ReadAllTextAsync(workspace.SourcePaths["Base.cs"]);
        var beforeDerived = await File.ReadAllTextAsync(workspace.SourcePaths["Derived.cs"]);
        var counts = PushMembersDownOperation.BuildLinkedPathCounts(workspace.Context.Solution);
        var derivedKey = RoslynMcp.Core.FileSystem.PathResolver.GetPathComparisonKey(
            workspace.SourcePaths["Derived.cs"]);
        Assert.True(counts.TryGetValue(derivedKey, out var derivedCount) && derivedCount > 1);

        var operation = new PushMembersDownOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["Base.cs"]
        });

        Assert.True(result.Success);
        Assert.Equal(beforeBase, await File.ReadAllTextAsync(workspace.SourcePaths["Base.cs"]));
        Assert.Equal(beforeDerived, await File.ReadAllTextAsync(workspace.SourcePaths["Derived.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
    }

    private const string LeaveAbstractMixedFile = """
        namespace TestApp;

        public class Animal
        {
            public virtual int Speak()
            {
                return 1;
            }

            public static int Count;
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_LeaveAbstractSkipsNonAbstractableMembers()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", LeaveAbstractMixedFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true,
            LeaveAbstract = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        // Speak becomes abstract on Animal and override on Dog; static Count stays.
        Assert.Contains("abstract", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Count", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Count", ExtractTypeBody(text, "Dog"));
    }

    [Fact]
    public void MemberCascadeKey_DistinguishesRefVersusValueOverloads()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Root
            {
                public void M(ref int i) { }
                public void M(int i) { }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "CascadeKeyRefTest",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var refKey = PushMembersDownOperation.MemberCascadeKey(model.GetDeclaredSymbol(methods[0])!);
        var valueKey = PushMembersDownOperation.MemberCascadeKey(model.GetDeclaredSymbol(methods[1])!);
        Assert.NotEqual(refKey, valueKey);
        Assert.Contains("ref", refKey, StringComparison.OrdinalIgnoreCase);
    }

    private const string SameDeclarationDependentFile = """
        namespace TestApp;

        public class Animal
        {
            private int _x;

            public int Get()
            {
                return _x;
            }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_PushesInterdependentMembersInSameDeclaration()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", SameDeclarationDependentFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        Assert.DoesNotContain("_x", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Get", ExtractTypeBody(text, "Animal"));
        Assert.Contains("_x", ExtractTypeBody(text, "Dog"));
        Assert.Contains("Get", ExtractTypeBody(text, "Dog"));
    }

    private const string RefCascadeRootFile = """
        namespace TestApp;

        public class Root
        {
            public int M(ref int i)
            {
                return i;
            }
        }

        public class Middle : Root
        {
            public int M(int i)
            {
                return i;
            }
        }
        """;

    private const string RefCascadeLeafFile = """
        namespace TestApp;

        public class Leaf : Middle
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_CascadeKeepsRefVersusValueOverloads()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Leaf.cs", RefCascadeLeafFile),
            ("Root.cs", RefCascadeRootFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var root = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        var leaf = await File.ReadAllTextAsync(workspace.SourcePaths["Leaf.cs"]);
        Assert.DoesNotContain("M(ref", ExtractTypeBody(root, "Root"));
        Assert.Contains("M(ref", ExtractTypeBody(root, "Middle"));
        Assert.DoesNotContain("M(int", ExtractTypeBody(root, "Middle"));
        Assert.Contains("M(int", ExtractTypeBody(leaf, "Leaf"));
        Assert.DoesNotContain("M(ref", ExtractTypeBody(leaf, "Leaf"));
    }

    private const string ExplicitBaseTypedReceiverFile = """
        namespace TestApp;

        public class Animal
        {
            public int X;

            public int Get(Animal other)
            {
                return other.X;
            }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsExplicitBaseTypedReceiverInBatch()
    {
        // Pushing X + Get together would leave Get(Animal other) => other.X on Dog
        // while X is removed from Animal — uncompilable. Explicit base-typed
        // receivers must fail validation even inside the push batch.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", ExplicitBaseTypedReceiverFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        Assert.Contains("X", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Get", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("X", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Get", ExtractTypeBody(text, "Dog"));
    }

    private const string CyclicCrossPartialPartA = """
        namespace TestApp;

        public partial class Animal
        {
            public int A => B;
        }

        public class Dog : Animal
        {
        }
        """;

    private const string CyclicCrossPartialPartB = """
        namespace TestApp;

        public partial class Animal
        {
            public int B => A;
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_PushesCyclicCrossPartialDependencies()
    {
        // A refs B and B refs A across partials. Declaration-local batches each
        // fail validation; a type-wide batch must accept the cycle and move both.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("AnimalA.cs", CyclicCrossPartialPartA),
            ("AnimalB.cs", CyclicCrossPartialPartB));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var animalA = await File.ReadAllTextAsync(workspace.SourcePaths["AnimalA.cs"]);
        var animalB = await File.ReadAllTextAsync(workspace.SourcePaths["AnimalB.cs"]);
        Assert.DoesNotContain("A =>", ExtractTypeBody(animalA, "Animal"));
        Assert.DoesNotContain("B =>", ExtractTypeBody(animalB, "Animal"));
        Assert.Contains("A =>", ExtractTypeBody(animalA, "Dog"));
        Assert.Contains("B =>", ExtractTypeBody(animalA, "Dog"));
    }

    private const string ObjectInitializerReceiverFile = """
        namespace TestApp;

        public class Animal
        {
            public int X;

            public Animal Make()
            {
                return new Animal { X = 1 };
            }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsObjectInitializerReceiverInBatch()
    {
        // Pushing X + Make together would leave `new Animal { X = 1 }` on Dog
        // after X is removed from Animal — uncompilable. Object-initializer
        // member names must not be treated as implicit this.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", ObjectInitializerReceiverFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        Assert.Contains("X", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Make", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("X", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Make", ExtractTypeBody(text, "Dog"));
    }

    private const string PostSubstitutionCollisionFile = """
        namespace TestApp;

        public class Root<T, U>
        {
            public string M(T value)
            {
                return "t";
            }

            public int M(U value)
            {
                return 1;
            }
        }

        public class Middle : Root<int, int>
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsPostSubstitutionBatchCollisions()
    {
        // string M(T) + int M(U) differ by return type (cascade keys differ) but
        // both become M(int) on Middle : Root<int,int>. Declaration-identity
        // collision check must reject (CS0111), not cascade keys.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Root.cs", PostSubstitutionCollisionFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        Assert.Contains("M(T", ExtractTypeBody(text, "Root"));
        Assert.Contains("M(U", ExtractTypeBody(text, "Root"));
        Assert.DoesNotContain("M(int", ExtractTypeBody(text, "Middle"));
        Assert.DoesNotContain("M(", ExtractTypeBody(text, "Middle"));
    }

    private const string PostSubstitutionParamsCollisionFile = """
        namespace TestApp;

        public class Root<T, U>
        {
            public int M(params T[] values)
            {
                return values.Length;
            }

            public int M(U[] values)
            {
                return values.Length;
            }
        }

        public class Middle : Root<int, int>
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsPostSubstitutionParamsArrayCollisions()
    {
        // params T[] and U[] both become int[] on Middle : Root<int,int>.
        // C# declaration identity ignores the params modifier → CS0111.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Root.cs", PostSubstitutionParamsCollisionFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        Assert.Contains("params T", ExtractTypeBody(text, "Root"));
        Assert.Contains("U[]", ExtractTypeBody(text, "Root"));
        Assert.DoesNotContain("M(", ExtractTypeBody(text, "Middle"));
    }

    private const string GenericMethodTargetConflictFile = """
        namespace TestApp;

        public class Root<T>
        {
            public int M(T value)
            {
                return 0;
            }
        }

        public class Middle : Root<int>
        {
            public int M(int value)
            {
                return 1;
            }
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_GenericMethod_ConflictsWithSubstitutedSignature()
    {
        // Root<T>.M(T) becomes M(int) on Middle : Root<int>, which already
        // declares M(int). MemberAsSeenFromTarget must substitute methods
        // (not only indexers) so CanMoveMember rejects before CS0111.
        await using var workspace = await TempWorkspace.CreateAsync(GenericMethodTargetConflictFile);
        var operation = new PushMembersDownOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Root",
                Members = ["M"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsGenericMethodTargetConflict()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Root.cs", GenericMethodTargetConflictFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        Assert.Contains("M(T", ExtractTypeBody(text, "Root"));
        Assert.Contains("M(int", ExtractTypeBody(text, "Middle"));
        // Only Middle's original M(int) — Root's M must not be copied.
        Assert.Equal(1, CountOccurrences(ExtractTypeBody(text, "Middle"), "M(int"));
    }

    private const string PostSubstitutionRefOutCollisionFile = """
        namespace TestApp;

        public class Root<T, U>
        {
            public void M(ref T value)
            {
            }

            public void M(out U value)
            {
                value = default!;
            }
        }

        public class Middle : Root<int, int>
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsPostSubstitutionRefOutCollisions()
    {
        // M(ref T) + M(out U) both close to int on Middle : Root<int,int>.
        // C# forbids overloads that differ only by ref/in/out (CS0663) —
        // by-ref modes must share one collision key.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Root.cs", PostSubstitutionRefOutCollisionFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        Assert.Contains("ref T", ExtractTypeBody(text, "Root"));
        Assert.Contains("out U", ExtractTypeBody(text, "Root"));
        Assert.DoesNotContain("M(", ExtractTypeBody(text, "Middle"));
    }

    private const string DistinctGenericArityMethodsFile = """
        namespace TestApp;

        public class Root
        {
            public int M<T>(int value)
            {
                return value;
            }

            public int M<T, U>(int value)
            {
                return value;
            }
        }

        public class Middle : Root
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_DistinctGenericArity_DoesNotConflict()
    {
        // M<T>(int) and M<T,U>(int) are distinct by arity; collision keys must
        // include type-parameter count so both can be pushed together.
        await using var workspace = await TempWorkspace.CreateAsync(DistinctGenericArityMethodsFile);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Root",
            Members = ["M"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("M<", ExtractTypeBody(updated, "Root"));
        Assert.Contains("M<T>(", ExtractTypeBody(updated, "Middle"));
        Assert.Contains("M<T, U>(", ExtractTypeBody(updated, "Middle"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_PushesDistinctGenericArityMethods()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Root.cs", DistinctGenericArityMethodsFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Root.cs"]);
        Assert.DoesNotContain("M<", ExtractTypeBody(text, "Root"));
        Assert.Contains("M<T>(", ExtractTypeBody(text, "Middle"));
        Assert.Contains("M<T, U>(", ExtractTypeBody(text, "Middle"));
    }

    private const string ExplicitElementAccessReceiverFile = """
        namespace TestApp;

        public class Animal
        {
            public int this[int i] => i;

            public int Read(Animal other)
            {
                return other[0];
            }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsExplicitElementAccessReceiverInBatch()
    {
        // Pushing indexer + Read together would leave Read(Animal other) =>
        // other[0] on Dog after the indexer is removed from Animal —
        // uncompilable. Element-access receivers must not fall through as
        // implicit this.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", ExplicitElementAccessReceiverFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        Assert.Contains("this[", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Read", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("this[", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Read", ExtractTypeBody(text, "Dog"));
    }

    private const string ImplicitElementAccessReceiverFile = """
        namespace TestApp;

        public class Animal
        {
            public int this[int i]
            {
                get => i;
                set { }
            }

            public Animal Make()
            {
                return new Animal { [0] = 1 };
            }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsImplicitElementAccessReceiverInBatch()
    {
        // Pushing indexer + Make would leave `new Animal { [0] = 1 }` on Dog
        // after the indexer leaves Animal. ImplicitElementAccess receivers
        // must resolve like object-initializer member names.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", ImplicitElementAccessReceiverFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        Assert.Contains("this[", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Make", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("this[", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Make", ExtractTypeBody(text, "Dog"));
    }

    private const string NestedObjectInitializerReceiverFile = """
        namespace TestApp;

        public class Holder
        {
            public Animal Child = new Animal();
        }

        public class Animal
        {
            public int X;

            public Holder Make()
            {
                return new Holder { Child = { X = 1 } };
            }
        }

        public class Dog : Animal
        {
        }
        """;

    [SkippableFact]
    public async Task PushMembersDown_AllFilesTrue_RejectsNestedObjectInitializerReceiverInBatch()
    {
        // Nested member initializer `Child = { X = 1 }` binds X to Animal via
        // Holder.Child, not implicit this. A batch of X + Make must still reject.
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Types.cs", NestedObjectInitializerReceiverFile));
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePaths["Types.cs"]);
        Assert.Contains("X", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Make", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("X", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Make", ExtractTypeBody(text, "Dog"));
    }

    #endregion

    #region Helpers

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(value, start, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            start = index + value.Length;
        }
    }

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
                "PushMembersDownCompileTest",
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
        Assert.True(errors.Count == 0, "Generated push_members_down members did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private static (IPropertySymbol Indexer, INamedTypeSymbol SameAssemblyType, INamedTypeSymbol OtherAssemblyType)
        CompileProtectedInternalIndexerPair()
    {
        var libTree = CSharpSyntaxTree.ParseText("""
            public class Animal
            {
                protected internal string this[int i] { get => ""; set { } }
            }
            """);
        var lib = CSharpCompilation.Create(
            "PushDownLib",
            new[] { libTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var app = CSharpCompilation.Create(
            "PushDownApp",
            new[] { CSharpSyntaxTree.ParseText("public class Dog : Animal { }") },
            new MetadataReference[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                lib.ToMetadataReference()
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var animal = lib.GetTypeByMetadataName("Animal");
        Assert.NotNull(animal);
        var indexer = animal!.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);
        var dog = app.GetTypeByMetadataName("Dog");
        Assert.NotNull(dog);
        return (indexer, animal, dog!);
    }

    private static (IPropertySymbol Indexer, INamedTypeSymbol SameAssemblyType, INamedTypeSymbol OtherAssemblyType, IndexerDeclarationSyntax Syntax)
        CompilePublicIndexerWithProtectedInternalSetter()
    {
        var libTree = CSharpSyntaxTree.ParseText("""
            public class Animal
            {
                public string this[int i] { get => ""; protected internal set { } }
            }
            """);
        var lib = CSharpCompilation.Create(
            "PushDownLibSetter",
            new[] { libTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var app = CSharpCompilation.Create(
            "PushDownAppSetter",
            new[] { CSharpSyntaxTree.ParseText("public class Dog : Animal { }") },
            new MetadataReference[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                lib.ToMetadataReference()
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var animal = lib.GetTypeByMetadataName("Animal");
        Assert.NotNull(animal);
        var indexer = animal!.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);
        var dog = app.GetTypeByMetadataName("Dog");
        Assert.NotNull(dog);
        var syntax = libTree.GetCompilationUnitRoot().DescendantNodes().OfType<IndexerDeclarationSyntax>().Single();
        return (indexer, animal, dog!, syntax);
    }

    private static (IPropertySymbol Property, INamedTypeSymbol SameAssemblyType, INamedTypeSymbol OtherAssemblyType, PropertyDeclarationSyntax Syntax)
        CompilePublicPropertyWithProtectedInternalSetter()
    {
        var libTree = CSharpSyntaxTree.ParseText("""
            public class Animal
            {
                public int Width { get => 0; protected internal set { } }
            }
            """);
        var lib = CSharpCompilation.Create(
            "PushDownLibPropSetter",
            new[] { libTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var app = CSharpCompilation.Create(
            "PushDownAppPropSetter",
            new[] { CSharpSyntaxTree.ParseText("public class Dog : Animal { }") },
            new MetadataReference[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                lib.ToMetadataReference()
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var animal = lib.GetTypeByMetadataName("Animal");
        Assert.NotNull(animal);
        var property = animal!.GetMembers().OfType<IPropertySymbol>().Single(p => p.Name == "Width");
        var dog = app.GetTypeByMetadataName("Dog");
        Assert.NotNull(dog);
        var syntax = libTree.GetCompilationUnitRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();
        return (property, animal, dog!, syntax);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public IReadOnlyDictionary<string, string> SourcePaths { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string LibraryPath { get; init; } = "";
        public string DerivedPath { get; init; } = "";
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPushMembersDown_" + Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// Lib project referenced by App. <see cref="LibraryPath"/> is the
        /// base type; <see cref="DerivedPath"/> / <see cref="SourcePath"/>
        /// is the derived type until the caller picks the base file.
        /// </summary>
        public static async Task<TempWorkspace> CreateReferencedLibraryAsync(string librarySource, string appSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPushMembersDownXP_" + Guid.NewGuid().ToString("N"));
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
                    SourcePath = libSource,
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


        /// <summary>
        /// ProjectA owns Base.cs alone; Derived.cs is linked into ProjectA and
        /// ProjectB (ProjectB references ProjectA). Used to prove sourceFile-
        /// filtered walks still see multi-view derived declaring paths.
        /// </summary>
        public static async Task<TempWorkspace> CreateWithLinkedDerivedAsync(
            string baseSource,
            string derivedSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPushMembersDownLinked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            var basePath = Path.Combine(directory, "Base.cs");
            var derivedPath = Path.Combine(directory, "Derived.cs");
            var projectAPath = Path.Combine(directory, "ProjectA.csproj");
            var projectBPath = Path.Combine(directory, "ProjectB.csproj");
            var projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            var projectAGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectBGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

            await File.WriteAllTextAsync(basePath, baseSource);
            await File.WriteAllTextAsync(derivedPath, derivedSource);
            await File.WriteAllTextAsync(solutionPath, $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{{projectTypeGuid}}") = "ProjectA", "ProjectA.csproj", "{{projectAGuid}}"
                EndProject
                Project("{{projectTypeGuid}}") = "ProjectB", "ProjectB.csproj", "{{projectBGuid}}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                		Release|Any CPU = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{{projectAGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectAGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectAGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectAGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectBGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectBGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                	EndGlobalSection
                EndGlobal
                """);

            await File.WriteAllTextAsync(projectAPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Base.cs" />
                    <Compile Include="Derived.cs" Link="Derived.cs" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(projectBPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="ProjectA.csproj" />
                    <Compile Include="Derived.cs" Link="Derived.cs" />
                  </ItemGroup>
                </Project>
                """);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                if (context.GetDocumentByPath(basePath) == null ||
                    context.GetDocumentByPath(derivedPath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Base/Derived sources.");
                }

                var linkedViews = context.Solution.Projects
                    .SelectMany(p => p.Documents)
                    .Count(d => string.Equals(
                        Path.GetFullPath(d.FilePath!),
                        Path.GetFullPath(derivedPath),
                        StringComparison.OrdinalIgnoreCase));
                if (linkedViews < 2)
                {
                    context.Dispose();
                    throw new InvalidOperationException(
                        $"Expected Derived.cs linked into 2 projects, found {linkedViews}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = solutionPath,
                    SourcePath = basePath,
                    SourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Base.cs"] = basePath,
                        ["Derived.cs"] = derivedPath
                    },
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

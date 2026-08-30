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
/// Operation-level tests for <see cref="PushMembersDownOperation"/>, including optional
/// <c>line</c>, <c>targetDerivedTypes</c>, <c>leaveAbstract</c>, and <c>members</c>.
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

            public class Dog : Animal
            {
            }

            public /* nested-animal */ class Animal
            {
                public int Age { get; set; }

                public class Puppy : Animal
                {
                }
            }
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

        Assert.True(PushMembersDownOperation.SpanCoversLine(span, 1));
        Assert.True(PushMembersDownOperation.SpanCoversLine(span, 2));
        Assert.False(PushMembersDownOperation.SpanCoversLine(span, 3));
        Assert.False(PushMembersDownOperation.SpanCoversLine(span, 0));
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

    #region Helpers

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
        public string LibraryPath { get; init; } = "";
        public string DerivedPath { get; init; } = "";
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPushMembersDown_" + Guid.NewGuid().ToString("N"));
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

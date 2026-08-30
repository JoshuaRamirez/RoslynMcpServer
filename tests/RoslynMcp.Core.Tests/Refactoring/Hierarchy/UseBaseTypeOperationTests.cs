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
/// Operation-level tests for <see cref="UseBaseTypeOperation"/>, including optional
/// <c>line</c>, <c>targetBaseType</c>, and <c>preview</c>.
/// </summary>
public class UseBaseTypeOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = "",
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
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
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = "Types.cs",
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 optional line disambiguation

    private const string NestedSameNameDogSource = """
        namespace TestApp;

        public class Animal
        {
            public int Eat() => 1;
        }

        public /* outer-dog */ class Dog : Animal
        {
            public /* nested-dog */ class Dog : Animal
            {
            }
        }

        public static class OuterUse
        {
            public static int Feed(Dog dog) => dog.Eat();
        }

        public static class NestedUse
        {
            public static int Feed(Dog.Dog dog) => dog.Eat();
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
                public int Eat() => 1;
            }

            public /* dog-class */ class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
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
                public int Eat() => 1;
            }

            public /* dog-class */ class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
        }
        """;

    private const string LaterSameNamedDogSource = """
        namespace Other
        {
            public class Animal
            {
                public int Eat() => 1;
            }

            public /* first-dog */ class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
        }

        namespace TestApp
        {
            public class Animal
            {
                public int Eat() => 1;
            }

            public /* later-dog */ class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
        }
        """;

    private const string QualifiedSameNamedDogSource = """
        namespace A
        {
            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
        }

        namespace B
        {
            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
        }
        """;

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new UseBaseTypeParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Dog"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_OmittedLine_KeepsTypeNameFirstMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
        Assert.Contains("public static int Feed(Dog.Dog dog) => dog.Eat();", updated);
        Assert.DoesNotContain("Feed(Dog dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_OmittedLine_FqnSemanticPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(QualifiedSameNamedDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "B.Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace A", updated);
        Assert.Contains("public static int Feed(Dog dog) => dog.Eat();", updated);
        Assert.Contains("namespace B", updated);
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = FindLine(NestedSameNameDogSource, "nested-dog")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Feed(Dog dog) => dog.Eat();", updated);
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
        Assert.DoesNotContain("Feed(Dog.Dog dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = FindLine(NestedSameNameDogSource, "outer-dog")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
        Assert.Contains("public static int Feed(Dog.Dog dog) => dog.Eat();", updated);
        Assert.DoesNotContain("Feed(Dog dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Line = FindLine(EnumFirstThenSameNamedClassSource, "dog-enum")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.Contains("Feed(Dog dog)", NormalizeNewlines(updated));
    }

    [SkippableFact]
    public async Task UseBaseType_LineOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.Contains("Feed(Dog dog)", NormalizeNewlines(updated));
    }

    [SkippableFact]
    public async Task UseBaseType_SequentialCalls_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(LaterSameNamedDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = FindLine(LaterSameNamedDogSource, "first-dog")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Other", afterFirst);
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", afterFirst);
        Assert.Contains("namespace TestApp", afterFirst);
        Assert.Contains("public static int Feed(Dog dog) => dog.Eat();", afterFirst);

        var second = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = FindLine(afterFirst, "later-dog")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Other", updated);
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
        Assert.Contains("namespace TestApp", updated);
        Assert.DoesNotContain("Feed(Dog dog)", updated);
        var otherUse = updated[updated.IndexOf("namespace Other", StringComparison.Ordinal)..
            updated.IndexOf("namespace TestApp", StringComparison.Ordinal)];
        var testAppUse = updated[updated.IndexOf("namespace TestApp", StringComparison.Ordinal)..];
        Assert.Contains("Feed(Animal dog)", otherUse);
        Assert.Contains("Feed(Animal dog)", testAppUse);
    }

    [SkippableFact]
    public async Task UseBaseType_Line_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = FindLine(NestedSameNameDogSource, "nested-dog"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Dog", StringComparison.Ordinal)
            && c.Description.Contains("Animal", StringComparison.Ordinal));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("Animal", StringComparison.Ordinal));
        Assert.Contains(result.PendingChanges, c =>
            c.BeforeSnippet != null && c.BeforeSnippet.Contains("Dog.Dog", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, c => c.BeforeSnippet == "Dog");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstMatchPicksOuter()
    {
        var (root, model) = Compile(NestedSameNameDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "Dog", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FqnPicksMatchingNamespace()
    {
        var (root, model) = Compile(QualifiedSameNamedDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "B.Dog", line: null);

        Assert.NotNull(found);
        var type = Assert.IsType<ClassDeclarationSyntax>(found);
        Assert.Equal("B", type.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Name.ToString());
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var (root, model) = Compile(NestedSameNameDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", FindLine(NestedSameNameDogSource, "nested-dog"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Dog");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var (root, model) = Compile(NestedSameNameDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", FindLine(NestedSameNameDogSource, "outer-dog"));

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
            }

            public class Dog // nested-dog
            {
            }
            """;

        var (root, model) = Compile(source);
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-dog");
        Assert.NotEqual(startLine, identifierLine);

        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "Dog", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var (root, model) = Compile(NestedSameNameDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "Dog", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksClass()
    {
        var (root, model) = Compile(EnumFirstThenSameNamedClassSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "Dog", line: null);

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
                }
            }
            """;

        var (root, model) = Compile(source);
        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "Dog", line: null);

        Assert.NotNull(found);
        Assert.IsType<StructDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var (root, model) = Compile(EnumFirstThenSameNamedClassSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", FindLine(EnumFirstThenSameNamedClassSource, "dog-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var (root, model) = Compile(EnumFirstThenSameNamedClassSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", FindLine(EnumFirstThenSameNamedClassSource, "dog-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var (root, model) = Compile(DelegateFirstThenSameNamedClassSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "Dog", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var (root, model) = Compile(DelegateFirstThenSameNamedClassSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate"));

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

        Assert.True(UseBaseTypeOperation.SpanCoversLine(span, 1));
        Assert.True(UseBaseTypeOperation.SpanCoversLine(span, 2));
        Assert.False(UseBaseTypeOperation.SpanCoversLine(span, 3));
        Assert.False(UseBaseTypeOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task UseBaseType_OmittedLine_EnumFirstThenSameNamedClass_RewritesClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("enum Dog", updated);
        Assert.Contains("Feed(Animal dog)", updated);
        Assert.DoesNotContain("Feed(Dog dog)", updated);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task UseBaseType_ParameterUsingOnlyBaseMembers_RewritesToBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Feed(Animal dog)", NormalizeNewlines(updated));
        Assert.DoesNotContain("Feed(Dog dog)", NormalizeNewlines(updated));
        Assert.Contains("class Dog : Animal", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_InterfaceBase_RewritesCompatibleParameter()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Eat();
            }

            public class Dog : IAnimal
            {
                public int Eat()
                {
                    return 1;
                }

                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            TargetBaseType = "IAnimal"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Feed(IAnimal dog)", NormalizeNewlines(updated));
    }

    [SkippableFact]
    public async Task UseBaseType_MixedUsages_RewritesOnlyCompatibleReferences()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }

                public static int Speak(Dog dog)
                {
                    return dog.Bark();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(Animal dog)", updated);
        Assert.Contains("Speak(Dog dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_ExplicitTarget_UsesNamedBase()
    {
        const string source = """
            namespace TestApp;

            public class Creature
            {
                public int Live()
                {
                    return 1;
                }
            }

            public class Animal : Creature
            {
                public int Eat()
                {
                    return 2;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Keep(Dog dog)
                {
                    return dog.Live();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            TargetBaseType = "Creature"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Keep(Creature dog)", NormalizeNewlines(updated));
    }

    [SkippableFact]
    public async Task UseBaseType_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null && change.AfterSnippet.Contains("Animal"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task UseBaseType_LocalVariable_RewritesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed()
                {
                    Dog dog = new Dog();
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Animal dog = new Dog();", updated);
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task UseBaseType_NoBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog
            {
                public int Bark()
                {
                    return 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.NoCommonBase, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_MissingNamedBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                TargetBaseType = "Creature"
            }));

        Assert.Equal(ErrorCodes.BaseClassNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_NoEligibleReferences_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static object Create()
                {
                    return new Dog();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.NoEligibleReferences, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_BaseCannotSatisfyUsedMembers_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Speak(Dog dog)
                {
                    return dog.Bark();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [Fact]
    public void UseBaseType_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_TypeNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_TargetTypedNew_IsNotRewritten()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed()
                {
                    Dog dog = new();
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_QualifiedTypeName_SelectsMatchingNamespace()
    {
        const string source = """
            namespace A
            {
                public class Animal
                {
                    public int Eat() => 1;
                }

                public class Dog : Animal
                {
                    public int Bark() => 2;
                }

                public static class Use
                {
                    public static int Feed(Dog dog) => dog.Eat();
                }
            }

            namespace B
            {
                public class Animal
                {
                    public int Eat() => 1;
                }

                public class Dog : Animal
                {
                    public int Bark() => 2;
                }

                public static class Use
                {
                    public static int Feed(Dog dog) => dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "B.Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace A", updated);
        Assert.Contains("public static int Feed(Dog dog) => dog.Eat();", updated);
        Assert.Contains("namespace B", updated);
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_TargetTypedNewReturn_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static Dog Create() => new();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_ConflictingSimpleName_QualifiesReplacement()
    {
        const string source = """
            namespace Other
            {
                public class Animal
                {
                    public int Eat() => 1;
                }
            }

            namespace TestApp
            {
                public class Animal
                {
                }

                public class Dog : Other.Animal
                {
                }

                public static class Use
                {
                    public static int Feed(Dog dog) => dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "TestApp.Dog",
            TargetBaseType = "Animal"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(Other.Animal dog)", updated);
        Assert.DoesNotContain("Feed(Animal dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_OverrideParameter_IsNotRewritten()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public abstract class BaseHandler
            {
                public abstract int Feed(Dog dog);
            }

            public class Handler : BaseHandler
            {
                public override int Feed(Dog dog) => dog.Eat();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_GenericBaseMapping_UsesConstructedArguments()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Base<T>
            {
                public T Value { get; set; } = default!;
            }

            public class Dog<T> : Base<List<T>>
            {
            }

            public static class Use
            {
                public static int Count(Dog<int> dog) => dog.Value.Count;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Count(Base<List<int>> dog)", updated);
        Assert.DoesNotContain("Count(Base<int> dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_TargetTypedNewAssignment_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static Dog GetDog() => new Dog();

                public static int Feed()
                {
                    Dog x = GetDog();
                    x = new();
                    return x.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_StructWithSingleInterface_UsesInterface()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Eat();
            }

            public struct Dog : IAnimal
            {
                public int Eat() => 1;
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(IAnimal dog)", updated);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

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

    private static (SyntaxNode Root, SemanticModel Model) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "UseBaseTypeFindType",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree.GetRoot(), compilation.GetSemanticModel(tree));
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpUseBaseType_" + Guid.NewGuid().ToString("N"));
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

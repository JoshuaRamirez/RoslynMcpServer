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
/// <c>line</c>, <c>column</c>, <c>targetBaseType</c>, <c>preview</c>, and
/// <c>allFiles</c>.
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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                AllFiles = false,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
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
        UseBaseTypeOperation.Validate(new UseBaseTypeParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                AllFiles = true,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
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
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    private const string SameLineNestedDogSource = """
        namespace TestApp;

        public class Animal
        {
            public int Eat() => 1;
        }

        public class Dog : Animal { public class Dog : Animal { } }

        public static class OuterUse
        {
            public static int Feed(Dog dog) => dog.Eat();
        }

        public static class NestedUse
        {
            public static int Feed(Dog.Dog dog) => dog.Eat();
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
        Assert.False(@params.AllFiles);
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
        var otherStart = updated.IndexOf("namespace Other", StringComparison.Ordinal);
        var testAppStart = updated.IndexOf("namespace TestApp", StringComparison.Ordinal);
        var otherUse = updated[otherStart..testAppStart];
        var testAppUse = updated[testAppStart..];
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

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new UseBaseTypeParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Dog"
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog",
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
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = typeName,
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_OmittedColumn_KeepsTypeNameFirstMatch()
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
    public async Task UseBaseType_OmittedColumn_FqnSemanticPick()
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
    public async Task UseBaseType_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
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
    public async Task UseBaseType_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public class");
        var column = ColumnOf(SameLineNestedDogSource, "Dog : Animal { }");

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Feed(Dog dog) => dog.Eat();", updated);
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
        Assert.DoesNotContain("Feed(Dog.Dog dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public class");
        var column = ColumnOf(SameLineNestedDogSource, "Dog : Animal { public class");

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
        Assert.Contains("public static int Feed(Dog.Dog dog) => dog.Eat();", updated);
        Assert.DoesNotContain("Feed(Dog dog)", updated);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstMatchPicksOuter()
    {
        var (root, model) = Compile(NestedSameNameDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FqnPicksMatchingNamespace()
    {
        var (root, model) = Compile(QualifiedSameNamedDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "B.Dog", line: null, column: null);

        Assert.NotNull(found);
        var type = Assert.IsType<ClassDeclarationSyntax>(found);
        Assert.Equal("B", type.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Name.ToString());
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_EnumFirstPicksClass()
    {
        var (root, model) = Compile(EnumFirstThenSameNamedClassSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line: null, column: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var (root, model) = Compile(SameLineNestedDogSource);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public class");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line, ColumnOf(SameLineNestedDogSource, "Dog : Animal { }"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Dog");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var (root, model) = Compile(SameLineNestedDogSource);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public class");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line, ColumnOf(SameLineNestedDogSource, "Dog : Animal { public class"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var (root, model) = Compile(SameLineNestedDogSource);
        var nestedColumn = ColumnOf(SameLineNestedDogSource, "Dog : Animal { }");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_FqnPicksMatchingNamespace()
    {
        var (root, model) = Compile(QualifiedSameNamedDogSource);
        var nestedColumn = ColumnOf(QualifiedSameNamedDogSource, "class Dog");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "B.Dog", line: null, nestedColumn);

        Assert.NotNull(found);
        var type = Assert.IsType<ClassDeclarationSyntax>(found);
        Assert.Equal("B", type.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Name.ToString());
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_EnumFirstPicksClass()
    {
        var (root, model) = Compile(EnumFirstThenSameNamedClassSource);
        var enumColumn = ColumnOf(EnumFirstThenSameNamedClassSource, "Dog");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line: null, enumColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_DelegateFirstPicksClass()
    {
        var (root, model) = Compile(DelegateFirstThenSameNamedClassSource);
        var delegateColumn = ColumnOf(DelegateFirstThenSameNamedClassSource, "Dog()");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line: null, delegateColumn);

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
                }
            }
            """;

        var (root, model) = Compile(source);
        var structColumn = ColumnOf(source, "Dog");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line: null, structColumn);

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
            }

            public class Dog // nested-dog
            {
            }
            """;

        var (root, model) = Compile(source);
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-dog");
        Assert.NotEqual(startLine, identifierLine);

        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", identifierLine, ColumnOf(source, "Dog // split-dog"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task UseBaseType_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat() => 1;
            }

            public class
                Dog // split-dog
            : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Dog");
        var identifierLine = FindLine(source, "split-dog");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = identifierLine,
            Column = ColumnOf(source, "Dog // split-dog")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(Animal dog)", updated);
        Assert.DoesNotContain("Feed(Dog dog)", updated);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnEnumIdentifier_PicksEnum()
    {
        const string source = """
            namespace TestApp { public enum Dog { Ready } public class Dog { } }
            """;

        var (root, model) = Compile(source);
        var line = FindLine(source, "public enum Dog");
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog", line, ColumnOf(source, "Dog { Ready }"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task UseBaseType_ColumnOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        const string source = """
            namespace TestApp { public class Animal { public int Eat() => 1; } public enum Dog { Ready } public class Dog : Animal { } public static class Use { public static int Feed(Dog dog) => dog.Eat(); } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Line = FindLine(source, "public enum Dog"),
                Column = ColumnOf(source, "Dog { Ready }")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.Contains("Feed(Dog dog)", NormalizeNewlines(updated));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnDelegateIdentifier_PicksDelegate()
    {
        var (root, model) = Compile(DelegateFirstThenSameNamedClassSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(
            root, model, "Dog",
            FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate"),
            ColumnOf(DelegateFirstThenSameNamedClassSource, "Dog()"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task UseBaseType_ColumnOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "dog-delegate"),
                Column = ColumnOf(DelegateFirstThenSameNamedClassSource, "Dog()")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.Contains("Feed(Dog dog)", NormalizeNewlines(updated));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var (root, model) = Compile(NestedSameNameDogSource);
        var found = UseBaseTypeOperation.FindTypeDeclaration(root, model, "Dog", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task UseBaseType_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task UseBaseType_ColumnAndLine_UnknownTypeName_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameDogSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task UseBaseType_Column_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public class");

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = line,
            Column = ColumnOf(SameLineNestedDogSource, "Dog : Animal { }"),
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

        Assert.True(UseBaseTypeOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(UseBaseTypeOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(UseBaseTypeOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(UseBaseTypeOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task UseBaseType_SequentialColumn_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedDogSource);
        var operation = new UseBaseTypeOperation(workspace.Context);
        var line = FindLine(SameLineNestedDogSource, "public class Dog : Animal { public class");

        var first = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = line,
            Column = ColumnOf(SameLineNestedDogSource, "Dog : Animal { public class")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", afterFirst);
        Assert.Contains("public static int Feed(Dog.Dog dog) => dog.Eat();", afterFirst);

        var second = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Line = FindLine(afterFirst, "public class Dog : Animal { public class"),
            Column = ColumnOf(afterFirst, "Dog : Animal { }")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(Animal dog)", updated);
        Assert.DoesNotContain("Feed(Dog dog)", updated);
        Assert.DoesNotContain("Feed(Dog.Dog dog)", updated);
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

    #region AllFiles

    private const string AnimalSource = """
        namespace TestApp;

        public class Animal
        {
            public int Eat() => 1;
        }

        public interface IAnimal
        {
            int Eat();
        }
        """;

    private const string EligibleDogSource = """
        namespace TestApp;

        public class Dog : Animal
        {
            public int Bark() => 2;
        }

        public static class DogUse
        {
            public static int Feed(Dog dog) => dog.Eat();
        }
        """;

    private const string EligibleCatSource = """
        namespace TestApp;

        public class Cat : Animal
        {
        }

        public static class CatUse
        {
            public static int Pet(Cat cat) => cat.Eat();
        }
        """;

    private const string IneligibleTypesSource = """
        namespace TestApp;

        public class Standalone
        {
        }

        public class Horse : Animal
        {
            public int Neigh() => 3;
        }

        public static class HorseUse
        {
            public static int Speak(Horse horse) => horse.Neigh();
        }

        public class Fish : Animal
        {
        }

        public static class FishUse
        {
            public static object Create() => new Fish();
        }
        """;

    private const string DogImplementsIAnimalSource = """
        namespace TestApp;

        public class Dog : Animal, IAnimal
        {
            public int Bark() => 2;
        }

        public static class DogUse
        {
            public static int Feed(Dog dog) => dog.Eat();
        }
        """;

    [SkippableFact]
    public async Task UseBaseType_AllFilesFalse_RewritesOnlySpecifiedType()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", AnimalSource),
            ("Dog.cs", EligibleDogSource),
            ("Cat.cs", EligibleCatSource));
        var operation = new UseBaseTypeOperation(workspace.Context);
        var beforeCat = await File.ReadAllTextAsync(workspace.SourcePaths["Cat.cs"]);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePaths["Dog.cs"],
            AllFiles = false,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedDog = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Dog.cs"]));
        Assert.Contains("Feed(Animal dog)", updatedDog);
        Assert.DoesNotContain("Feed(Dog dog)", updatedDog);
        Assert.Equal(beforeCat, await File.ReadAllTextAsync(workspace.SourcePaths["Cat.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["Dog.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Cat.cs"]));
    }

    [SkippableFact]
    public async Task UseBaseType_OmittedAllFiles_KeepsSingleSiteRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleDogSource + "\n" + AnimalSource.Replace("namespace TestApp;", ""));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(Animal dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_RewritesAcrossEligibleTypesAndFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", AnimalSource),
            ("Dog.cs", EligibleDogSource),
            ("Cat.cs", EligibleCatSource),
            ("Skip.cs", IneligibleTypesSource));
        var operation = new UseBaseTypeOperation(workspace.Context);
        var beforeSkip = await File.ReadAllTextAsync(workspace.SourcePaths["Skip.cs"]);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedDog = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Dog.cs"]));
        var updatedCat = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Cat.cs"]));
        Assert.Contains("Feed(Animal dog)", updatedDog);
        Assert.DoesNotContain("Feed(Dog dog)", updatedDog);
        Assert.Contains("Pet(Animal cat)", updatedCat);
        Assert.DoesNotContain("Pet(Cat cat)", updatedCat);
        Assert.Equal(beforeSkip, await File.ReadAllTextAsync(workspace.SourcePaths["Skip.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["Dog.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Cat.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Skip.cs"]));
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_WithoutSourceFileOrTypeName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", AnimalSource),
            ("Dog.cs", EligibleDogSource),
            ("Cat.cs", EligibleCatSource));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleDogSource + "\n" + AnimalSource.Replace("namespace TestApp;", ""));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                AllFiles = false,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesFalse_WithoutTypeName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleDogSource + "\n" + AnimalSource.Replace("namespace TestApp;", ""));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleDogSource + "\n" + AnimalSource.Replace("namespace TestApp;", ""));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                AllFiles = true,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleDogSource + "\n" + AnimalSource.Replace("namespace TestApp;", ""));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleDogSource + "\n" + AnimalSource.Replace("namespace TestApp;", ""));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task UseBaseType_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", AnimalSource),
            ("Dog.cs", EligibleDogSource),
            ("Cat.cs", EligibleCatSource),
            ("Skip.cs", IneligibleTypesSource));
        var operation = new UseBaseTypeOperation(workspace.Context);
        var beforeDog = await File.ReadAllTextAsync(workspace.SourcePaths["Dog.cs"]);
        var beforeCat = await File.ReadAllTextAsync(workspace.SourcePaths["Cat.cs"]);
        var beforeSkip = await File.ReadAllTextAsync(workspace.SourcePaths["Skip.cs"]);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["Dog.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["Cat.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["Skip.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("Animal", StringComparison.Ordinal));
        Assert.Equal(beforeDog, await File.ReadAllTextAsync(workspace.SourcePaths["Dog.cs"]));
        Assert.Equal(beforeCat, await File.ReadAllTextAsync(workspace.SourcePaths["Cat.cs"]));
        Assert.Equal(beforeSkip, await File.ReadAllTextAsync(workspace.SourcePaths["Skip.cs"]));
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_EveryTypeIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", AnimalSource),
            ("Skip.cs", IneligibleTypesSource));
        var operation = new UseBaseTypeOperation(workspace.Context);
        var beforeAnimal = await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]);
        var beforeSkip = await File.ReadAllTextAsync(workspace.SourcePaths["Skip.cs"]);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeAnimal, await File.ReadAllTextAsync(workspace.SourcePaths["Animal.cs"]));
        Assert.Equal(beforeSkip, await File.ReadAllTextAsync(workspace.SourcePaths["Skip.cs"]));
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_SkipsNoBaseNoEligibleRefsAndIncompatibleMembers()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", AnimalSource),
            ("Dog.cs", EligibleDogSource),
            ("Skip.cs", IneligibleTypesSource));
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedSkip = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Skip.cs"]));
        Assert.Contains("Speak(Horse horse)", updatedSkip);
        Assert.Contains("new Fish()", updatedSkip);
        Assert.Contains("class Standalone", updatedSkip);
        var updatedDog = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Dog.cs"]));
        Assert.Contains("Feed(Animal dog)", updatedDog);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task UseBaseType_AllFilesTrue_TargetBaseTypeFiltersTypesWithoutThatBase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Animal.cs", AnimalSource),
            ("Dog.cs", DogImplementsIAnimalSource),
            ("Cat.cs", EligibleCatSource));
        var operation = new UseBaseTypeOperation(workspace.Context);
        var beforeCat = await File.ReadAllTextAsync(workspace.SourcePaths["Cat.cs"]);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            AllFiles = true,
            TargetBaseType = "IAnimal"
        });

        Assert.True(result.Success);
        var updatedDog = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Dog.cs"]));
        Assert.Contains("Feed(IAnimal dog)", updatedDog);
        Assert.Equal(beforeCat, await File.ReadAllTextAsync(workspace.SourcePaths["Cat.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["Dog.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Cat.cs"]));
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal(
            "Replace derived-type reference with a compatible base type",
            UseBaseTypeOperation.BuildAllFilesDescription(1));
        Assert.Equal(
            "Replace 2 derived-type references with compatible base types",
            UseBaseTypeOperation.BuildAllFilesDescription(2));
    }

    [Fact]
    public void CollectTypeDeclarations_IncludesClassStructInterface_ExcludesEnumAndDelegate()
    {
        const string source = """
            namespace TestApp;
            public class C { }
            public struct S { }
            public interface I { }
            public enum E { A }
            public delegate void D();
            public record R();
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var types = UseBaseTypeOperation.CollectTypeDeclarations(root);
        Assert.Equal(4, types.Count);
        Assert.Contains(types, t => t.Identifier.Text == "C");
        Assert.Contains(types, t => t.Identifier.Text == "S");
        Assert.Contains(types, t => t.Identifier.Text == "I");
        Assert.Contains(types, t => t.Identifier.Text == "R");
        Assert.DoesNotContain(types, t => t.Identifier.Text == "E");
        Assert.DoesNotContain(types, t => t.Identifier.Text == "D");
    }

    #endregion

    #region Helpers

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

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
        public required IReadOnlyDictionary<string, string> FilePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public IReadOnlyDictionary<string, string> SourcePaths => FilePaths;

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateAsync(files);

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpUseBaseType_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            // Pin authored sources so generated AssemblyInfo / TFM attributes
            // are not hit by the allFiles .cs document walk.
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

            var filePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                filePaths[fileName] = path;
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
                    FilePaths = filePaths,
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

using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateEqualsHashCodeOperation"/>, including <c>implementIEquatable</c>, <c>generateOperators</c>, and <c>replaceExisting</c>.
/// </summary>
public class GenerateEqualsHashCodeOperationTests
{
    private const string PersonSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }
        }
        """;

    private const string PointSource = """
        namespace TestApp;

        public struct Point
        {
            public int X { get; set; }

            public int Y { get; set; }
        }
        """;

    #region Default / false — no IEquatable

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableOmitted_DoesNotAddIEquatable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        AssertDefaultEqualsShape(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableFalse_DoesNotAddIEquatable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = false
        });

        Assert.True(result.Success);
        AssertDefaultEqualsShape(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    #endregion

    #region true — IEquatable + typed Equals + object delegates

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_AddsInterfaceTypedEqualsAndDelegatingObjectEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_Struct_UsesNonNullableTypedEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointSource, "Point.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Point>", updated);
        Assert.Contains("public bool Equals(Point other)", updated);
        Assert.DoesNotContain("Equals(Point?", updated);
        AssertObjectEqualsDelegates(updated, "Point");
        Assert.Contains("public override int GetHashCode()", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_UserTypeNamedIEquatable_UsesGlobalQualifiedInterface()
    {
        const string source = """
            namespace TestApp;

            public interface IEquatable { }

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Widget>", updated);
        Assert.Contains("public bool Equals(Widget? other)", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_GenericClass_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public class Box<T>
            {
                public T Value { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Box.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Box<T>>", updated);
        Assert.Contains("public bool Equals(Box<T>? other)", updated);
        Assert.DoesNotContain("IEquatable<Box>", updated.Replace("IEquatable<Box<T>>", "", StringComparison.Ordinal));
        Assert.DoesNotContain("Equals(Box other)", updated);
        AssertObjectEqualsDelegates(updated, "Box<T>");
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_GenericStruct_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public struct Pair<T, U>
            {
                public T Left { get; set; }

                public U Right { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Pair.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Pair",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Pair<T, U>>", updated);
        Assert.Contains("public bool Equals(Pair<T, U> other)", updated);
        Assert.DoesNotContain("Equals(Pair<T, U>?", updated);
        AssertObjectEqualsDelegates(updated, "Pair<T, U>");
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_AlreadyListsUserMarkerIEquatable_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public interface IEquatable { }

            public class Widget : IEquatable
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Widget : IEquatable", updated);
        Assert.Contains("global::System.IEquatable<Widget>", updated);
        Assert.Contains("public bool Equals(Widget? other)", updated);
        AssertObjectEqualsDelegates(updated, "Widget");
        AssertNoOperators(updated);
    }

    #endregion

    #region generateOperators

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsOmitted_DoesNotAddOperators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsFalse_DoesNotAddOperators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_Class_AddsNullSafeOperatorsConsistentWithEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_Struct_UsesNonNullableOperatorParameters()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointSource, "Point.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override bool Equals(object?", updated);
        Assert.Contains("public override int GetHashCode()", updated);
        AssertEqualityOperators(updated, "Point", nullableParameters: false);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_GenericClass_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public class Box<T>
            {
                public T Value { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Box.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityOperators(updated, "Box<T>?", nullableParameters: true);
        Assert.DoesNotContain("operator ==(Box ", updated);
        Assert.DoesNotContain("operator ==(Box left", updated);
        Assert.DoesNotContain("operator !=(Box left", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_ExistingTwoArgEquals_UsesObjectEquals()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool Equals(Person? a, Person? b) => false;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static bool Equals(Person? a, Person? b)", updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
        var equality = ExtractMethod(updated, "public static bool operator ==");
        Assert.Contains("global::System.Object.Equals(left, right)", equality);
        Assert.DoesNotContain("return Equals(left, right)", equality);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableAndGenerateOperators_EmitsBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsFalse_TypeAlreadyHasOperators_DoesNotFailOrDuplicate()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(1, CountOccurrences(updated, "operator =="));
        Assert.Equal(1, CountOccurrences(updated, "operator !="));
        Assert.Contains("=> false", updated);
        Assert.Contains("=> true", updated);
        Assert.DoesNotContain("Object.Equals(left, right)", updated);
    }

    #endregion

    #region Preview

    [SkippableFact]
    public async Task GenerateEquals_PreviewDefault_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public override bool Equals(object?", snippet);
        Assert.DoesNotContain("IEquatable", snippet);
        Assert.DoesNotContain("Equals(Person", snippet);
        AssertNoOperators(snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_PreviewImplementIEquatableTrue_DoesNotWriteFiles_AndDescribesShape()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("IEquatable", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("global::System.IEquatable<Person>", snippet);
        Assert.Contains("public bool Equals(Person? other)", snippet);
        Assert.Contains("Equals(other)", snippet);
        AssertNoOperators(snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_PreviewGenerateOperatorsTrue_DoesNotWriteFiles_AndDescribesOperators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("equality operators", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("operator ==", snippet);
        Assert.Contains("operator !=", snippet);
        Assert.Contains("global::System.Object.Equals(left, right)", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Already implements / typed Equals

    [SkippableFact]
    public async Task GenerateEquals_AlreadyImplementsIEquatable_FailsWith3144()
    {
        const string source = """
            namespace TestApp;

            public class Person : System.IEquatable<Person>
            {
                public string Name { get; set; }

                public bool Equals(Person? other) => other is not null && Name == other.Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ImplementIEquatable = true
            }));

        Assert.Equal(ErrorCodes.AlreadyImplementsIEquatable, ex.ErrorCode);
        Assert.Equal("3144", ex.ErrorCode);
        Assert.Contains("IEquatable", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_AlreadyHasTypedEquals_FailsWith3144()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public bool Equals(Person? other) => other is not null && Name == other.Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ImplementIEquatable = true
            }));

        Assert.Equal(ErrorCodes.AlreadyImplementsIEquatable, ex.ErrorCode);
        Assert.Equal("3144", ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_AlreadyHasOperators_FailsWith3145()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                GenerateOperators = true
            }));

        Assert.Equal(ErrorCodes.AlreadyHasEqualityOperators, ex.ErrorCode);
        Assert.Equal("3145", ex.ErrorCode);
        Assert.Contains("operator", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_AlreadyHasEqualityOperatorOnly_FailsWith3145()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool operator ==(Person? left, Person? right) => false;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                GenerateOperators = true
            }));

        Assert.Equal(ErrorCodes.AlreadyHasEqualityOperators, ex.ErrorCode);
        Assert.Equal("3145", ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region replaceExisting

    private const string PersonWithEqualsSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public override bool Equals(object? obj) => false;

            public override int GetHashCode() => 42;
        }
        """;

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingOmitted_ExistingEquals_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Contains("Equals", ex.Message);
        Assert.Equal(PersonWithEqualsSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingFalse_ExistingEquals_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal(PersonWithEqualsSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_ReplacesEqualsAndGetHashCode()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> 42", updated);
        Assert.Contains("HashCode.Combine", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override bool Equals(object?"));
        Assert.Equal(1, CountOccurrences(updated, "public override int GetHashCode()"));
        AssertNoOperators(updated);
        Assert.DoesNotContain("IEquatable", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_ImplementIEquatable_ReplacesTypedEqualsAndInterface()
    {
        const string source = """
            namespace TestApp;

            public class Person : System.IEquatable<Person>
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public bool Equals(Person? other) => false;

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> 42", updated);
        Assert.Equal(1, CountOccurrences(updated, "IEquatable<Person>"));
        Assert.Equal(1, CountOccurrences(updated, "public bool Equals(Person? other)"));
        Assert.DoesNotContain(": System.IEquatable<Person>", updated);
        Assert.Contains("global::System.IEquatable<Person>", updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_GenerateOperators_ReplacesOperators()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
        Assert.Equal(1, CountOccurrences(updated, "operator =="));
        Assert.Equal(1, CountOccurrences(updated, "operator !="));
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> true", updated);
        Assert.DoesNotContain("=> 42", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_ExistingOperators_GenerateOperatorsFalse_LeavesOperators()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = false,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        Assert.Equal(1, CountOccurrences(updated, "operator =="));
        Assert.Equal(1, CountOccurrences(updated, "operator !="));
        Assert.Contains("=> false", updated);
        Assert.Contains("=> true", updated);
        Assert.DoesNotContain("Object.Equals(left, right)", updated);
        Assert.DoesNotContain("=> 42", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_Preview_DoesNotWriteFiles_AndDescribesReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            GenerateOperators = true,
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Replace", result.PendingChanges[0].Description);
        Assert.Contains("IEquatable", result.PendingChanges[0].Description);
        Assert.Contains("equality operators", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("global::System.IEquatable<Person>", snippet);
        Assert.Contains("public bool Equals(Person? other)", snippet);
        Assert.Contains("operator ==", snippet);
        Assert.Contains("operator !=", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_GenericBox_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public class Box<T> : System.IEquatable<Box<T>>
            {
                public T Value { get; set; }

                public bool Equals(Box<T>? other) => false;

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Box.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            ImplementIEquatable = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Box<T>>", updated);
        Assert.Contains("public bool Equals(Box<T>? other)", updated);
        Assert.DoesNotContain("IEquatable<Box>", updated.Replace("IEquatable<Box<T>>", "", StringComparison.Ordinal));
        Assert.DoesNotContain("Equals(Box other)", updated);
        AssertObjectEqualsDelegates(updated, "Box<T>");
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> 42", updated);
        Assert.Equal(1, CountOccurrences(updated, "IEquatable<Box<T>>"));
        Assert.Equal(1, CountOccurrences(updated, "public bool Equals(Box<T>? other)"));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_PartialOtherFile_RemovesOtherPartMembers()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string equalsPart = """
            namespace TestApp;

            public partial class Person
            {
                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.Equals.cs", equalsPart));
        var otherPath = workspace.PathFor("Person.Equals.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        AssertDefaultEqualsShape(selected);
        Assert.DoesNotContain("=> false", selected);
        Assert.DoesNotContain("=> 42", selected);
        Assert.Equal(1, CountOccurrences(selected, "public override bool Equals(object?"));
        Assert.Equal(1, CountOccurrences(selected, "public override int GetHashCode()"));
        Assert.DoesNotContain("public override bool Equals", other);
        Assert.DoesNotContain("public override int GetHashCode", other);
        Assert.DoesNotContain("=> false", other);
        Assert.DoesNotContain("=> 42", other);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string equalsPart = """
            namespace TestApp;

            public partial class Person
            {
                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.Equals.cs", equalsPart));
        var otherPath = workspace.PathFor("Person.Equals.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    #endregion

    #region Helpers

    private static void AssertDefaultEqualsShape(string text)
    {
        Assert.DoesNotContain("IEquatable", text);
        Assert.DoesNotContain("public bool Equals(Person", text);
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        Assert.Contains("obj is Person other", objectEquals);
        Assert.DoesNotContain("Equals(other)", objectEquals);
        Assert.Contains("public override int GetHashCode()", text);
    }

    private static void AssertIEquatableClassShape(string text)
    {
        Assert.Contains("global::System.IEquatable<Person>", text);
        Assert.Contains("public bool Equals(Person? other)", text);
        AssertObjectEqualsDelegates(text, "Person");
        Assert.Contains("public override int GetHashCode()", text);

        var typedEquals = ExtractMethod(text, "public bool Equals(Person? other)");
        Assert.Contains("other", typedEquals);
        Assert.Contains("Name", typedEquals);
        Assert.Contains("Age", typedEquals);
    }

    private static void AssertNoOperators(string text)
    {
        Assert.DoesNotContain("operator ==", text);
        Assert.DoesNotContain("operator !=", text);
    }

    private static void AssertEqualityOperators(string text, string parameterType, bool nullableParameters)
    {
        var equalitySignature = $"public static bool operator ==({parameterType} left, {parameterType} right)";
        var inequalitySignature = $"public static bool operator !=({parameterType} left, {parameterType} right)";
        Assert.Contains(equalitySignature, text);
        Assert.Contains(inequalitySignature, text);

        if (!nullableParameters)
        {
            Assert.DoesNotContain($"operator ==({parameterType}?", text);
            Assert.DoesNotContain($"operator !=({parameterType}?", text);
        }

        var equality = ExtractMethod(text, "public static bool operator ==");
        Assert.Contains("global::System.Object.Equals(left, right)", equality);

        var inequality = ExtractMethod(text, "public static bool operator !=");
        Assert.Contains("!(left == right)", inequality);
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

    private static void AssertObjectEqualsDelegates(string text, string typeName)
    {
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        Assert.Contains($"obj is {typeName} other", objectEquals);
        Assert.Contains("Equals(other)", objectEquals);
        Assert.DoesNotContain("other.Name", objectEquals);
        Assert.DoesNotContain("other.Age", objectEquals);
        Assert.DoesNotContain("other.X", objectEquals);
    }

    private static string ExtractMethod(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Generated source did not contain '{signature}':\n{text}");
        var open = text.IndexOf('{', start);
        Assert.True(open >= 0, $"Generated method '{signature}' had no body:\n{text}");
        var depth = 0;
        for (var i = open; i < text.Length; i++)
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

        return text[start..];
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateEquals_" + Guid.NewGuid().ToString("N"));
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

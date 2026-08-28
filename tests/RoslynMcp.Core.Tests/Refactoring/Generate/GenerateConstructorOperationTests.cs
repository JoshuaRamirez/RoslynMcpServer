using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Unit tests for GenerateConstructorOperation semantic validation,
/// plus operation-level tests for <c>includeProperties</c> and
/// <c>includeInheritedMembers</c>.
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

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

    private static string ExtractConstructor(string source, string typeName)
    {
        var marker = $"public {typeName}(";
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

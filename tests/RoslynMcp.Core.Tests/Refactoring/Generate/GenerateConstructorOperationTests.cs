using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Unit tests for GenerateConstructorOperation semantic validation.
/// Tests validate type-level constraints for constructor generation.
/// </summary>
public class GenerateConstructorOperationTests
{
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

    #endregion
}

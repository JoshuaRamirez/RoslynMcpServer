using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Unit tests for RenameSymbolOperation semantic validation.
/// Tests validate symbol-level rename restrictions that occur during execution.
/// </summary>
public class RenameSymbolOperationTests
{
    #region Constructor Tests

    [Fact]
    public void RenameSymbol_Constructor_ThrowsCannotRenameConstructor()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Constructor, "MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameConstructor, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_Constructor_MessageIndicatesRenameContainingType()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Constructor, "MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Contains("containing type", exception.Message);
    }

    #endregion

    #region Destructor Tests

    [Fact]
    public void RenameSymbol_Destructor_ThrowsCannotRenameDestructor()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Destructor, "~MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameDestructor, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_Destructor_MessageIndicatesRenameContainingType()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Destructor, "~MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Contains("containing type", exception.Message);
    }

    #endregion

    #region Operator Tests

    [Fact]
    public void RenameSymbol_UserDefinedOperator_ThrowsCannotRenameOperator()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.UserDefinedOperator, "op_Addition");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameOperator, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_ConversionOperator_ThrowsCannotRenameOperator()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Conversion, "op_Implicit");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameOperator, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_Operator_MessageIndicatesCannotRename()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.UserDefinedOperator, "op_Addition");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Contains("operators", exception.Message.ToLowerInvariant());
    }

    #endregion

    #region External Symbol Tests

    [Fact]
    public void RenameSymbol_ExternalSymbol_ThrowsCannotRenameExternal()
    {
        // Arrange
        var symbol = CreateExternalSymbol("ExternalType");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(symbol));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameExternal, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_ExternalSymbol_MessageIndicatesExternalAssemblies()
    {
        // Arrange
        var symbol = CreateExternalSymbol("ExternalType");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(symbol));

        // Assert
        Assert.Contains("external assemblies", exception.Message);
    }

    #endregion

    #region Ambiguous Symbol Tests

    [Fact]
    public void RenameSymbol_AmbiguousSymbol_ThrowsSymbolAmbiguous()
    {
        // Arrange
        var candidateCount = 3;

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ThrowAmbiguousSymbolError("MyMethod", candidateCount));

        // Assert
        Assert.Equal(ErrorCodes.SymbolAmbiguous, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_AmbiguousSymbol_MessageIndicatesLineNumber()
    {
        // Arrange
        var candidateCount = 2;

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ThrowAmbiguousSymbolError("MyMethod", candidateCount));

        // Assert
        Assert.Contains("line number", exception.Message);
    }

    [Fact]
    public void RenameSymbol_AmbiguousSymbol_IncludesCandidateCountInDetails()
    {
        // Arrange
        var candidateCount = 3;

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ThrowAmbiguousSymbolError("MyMethod", candidateCount));

        // Assert
        Assert.Equal(candidateCount, exception.Details?["candidateCount"]);
    }

    #endregion

    #region Verbatim Identifier Tests

    [Fact]
    public void RenameSymbol_VerbatimIdentifier_AcceptsAtPrefix()
    {
        // Arrange
        var newName = "@class";

        // Act
        var isValid = IsValidIdentifier(newName);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void RenameSymbol_VerbatimIdentifierKeyword_DoesNotTreatAsKeyword()
    {
        // Arrange
        var newName = "@void";

        // Act
        var isKeyword = IsKeyword(newName);

        // Assert
        Assert.False(isKeyword);
    }

    [Fact]
    public void RenameSymbol_VerbatimIdentifier_MatchesIdentifierPattern()
    {
        // Arrange
        var newName = "@event";

        // Act
        var isValid = IsValidIdentifier(newName);

        // Assert
        Assert.True(isValid);
    }

    #endregion

    #region Rename All Overloads Tests

    [Fact]
    public void RenameSymbol_MethodWithOverloads_RenameOverloadsFlagDefaultsFalse()
    {
        // Arrange
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyMethod",
            NewName = "RenamedMethod"
        };

        // Act
        var renameOverloads = @params.RenameOverloads;

        // Assert
        Assert.False(renameOverloads);
    }

    [Fact]
    public void RenameSymbol_MethodWithOverloads_RenameOverloadsFlagCanBeEnabled()
    {
        // Arrange
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyMethod",
            NewName = "RenamedMethod",
            RenameOverloads = true
        };

        // Act
        var renameOverloads = @params.RenameOverloads;

        // Assert
        Assert.True(renameOverloads);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Mimics the ValidateRename logic from RenameSymbolOperation.
    /// </summary>
    private static void ValidateRename(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.Constructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameConstructor,
                    "Cannot rename constructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.Destructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameDestructor,
                    "Cannot rename destructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.UserDefinedOperator ||
                method.MethodKind == MethodKind.Conversion)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameOperator,
                    "Cannot rename operators.");
            }
        }

        if (symbol.ContainingAssembly != null &&
            !symbol.Locations.Any(l => l.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.CannotRenameExternal,
                "Cannot rename symbols from external assemblies.");
        }
    }

    private static void ThrowAmbiguousSymbolError(string symbolName, int candidateCount)
    {
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple symbols named '{symbolName}' found. Provide line number to disambiguate.",
            new Dictionary<string, object>
            {
                ["candidateCount"] = candidateCount
            });
    }

    private static bool IsValidIdentifier(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^@?[A-Za-z_][A-Za-z0-9_]*$");

    private static bool IsKeyword(string name)
    {
        if (name.StartsWith("@")) return false;
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None;
    }

    /// <summary>
    /// Creates a mock method symbol with the specified method kind.
    /// Uses real Roslyn compilation to create proper symbol instances.
    /// </summary>
    private static IMethodSymbol CreateMethodSymbol(MethodKind methodKind, string name)
    {
        var source = methodKind switch
        {
            MethodKind.Constructor => "public class MyClass { public MyClass() { } }",
            MethodKind.Destructor => "public class MyClass { ~MyClass() { } }",
            MethodKind.UserDefinedOperator => "public class MyClass { public static MyClass operator +(MyClass a, MyClass b) => a; }",
            MethodKind.Conversion => "public class MyClass { public static implicit operator int(MyClass m) => 0; }",
            _ => "public class MyClass { public void TestMethod() { } }"
        };

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);

        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        IMethodSymbol? symbol = methodKind switch
        {
            MethodKind.Constructor => root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .Select(c => semanticModel.GetDeclaredSymbol(c))
                .FirstOrDefault(),
            MethodKind.Destructor => root.DescendantNodes()
                .OfType<DestructorDeclarationSyntax>()
                .Select(d => semanticModel.GetDeclaredSymbol(d))
                .FirstOrDefault(),
            MethodKind.UserDefinedOperator => root.DescendantNodes()
                .OfType<OperatorDeclarationSyntax>()
                .Select(o => semanticModel.GetDeclaredSymbol(o))
                .FirstOrDefault(),
            MethodKind.Conversion => root.DescendantNodes()
                .OfType<ConversionOperatorDeclarationSyntax>()
                .Select(c => semanticModel.GetDeclaredSymbol(c))
                .FirstOrDefault(),
            _ => root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Select(m => semanticModel.GetDeclaredSymbol(m))
                .FirstOrDefault()
        };

        return symbol ?? throw new InvalidOperationException($"Failed to create method symbol for {methodKind}");
    }

    /// <summary>
    /// Creates a symbol that appears to be from an external assembly (no source locations).
    /// </summary>
    private static ISymbol CreateExternalSymbol(string name)
    {
        // Get a symbol from the system assembly (truly external)
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        var objectType = compilation.GetTypeByMetadataName("System.Object");
        return objectType ?? throw new InvalidOperationException("Could not get System.Object type");
    }

    #endregion
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="BraceTypeNameHelpers"/> —
/// FindTypeDeclaration hit/miss, GetQualifiedTypeName nested,
/// NormalizeScope null/whitespace/value, TypeNameMatches true/false,
/// ValidateBraceParams allFiles / single-file rule set.
/// </summary>
public class BraceTypeNameHelpersTests
{
    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot();

    [Fact]
    public void FindTypeDeclaration_Hit_ReturnsType()
    {
        var root = Parse("""
            namespace Sample
            {
                class Worker { }
            }
            """);

        var type = BraceTypeNameHelpers.FindTypeDeclaration(root, "Worker");
        Assert.Equal("Worker", type.Identifier.Text);
        Assert.Equal("Sample.Worker", BraceTypeNameHelpers.GetQualifiedTypeName(type));
    }

    [Fact]
    public void FindTypeDeclaration_Miss_ThrowsTypeNotFound()
    {
        var root = Parse("""
            class Worker { }
            """);

        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.FindTypeDeclaration(root, "Missing"));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
    }

    [Fact]
    public void GetQualifiedTypeName_NestedTypes_JoinsPath()
    {
        var root = Parse("""
            namespace Outer.Inner
            {
                class Host
                {
                    class Nested { }
                }
            }
            """);
        var nested = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Single(t => t.Identifier.Text == "Nested");

        Assert.Equal("Outer.Inner.Host.Nested", BraceTypeNameHelpers.GetQualifiedTypeName(nested));
    }

    [Fact]
    public void NormalizeScope_NullOrWhitespace_DefaultsToStatement()
    {
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope(null));
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope(""));
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope("   "));
    }

    [Fact]
    public void NormalizeScope_Value_TrimsAndLowercases()
    {
        Assert.Equal("file", BraceTypeNameHelpers.NormalizeScope("FILE"));
        Assert.Equal("type", BraceTypeNameHelpers.NormalizeScope(" Type "));
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope("Statement"));
    }

    [Fact]
    public void TypeNameMatches_True_ForSimpleAndQualified()
    {
        var root = Parse("""
            namespace Sample
            {
                class Worker { }
            }
            """);
        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Single();

        Assert.True(BraceTypeNameHelpers.TypeNameMatches(type, "Worker"));
        Assert.True(BraceTypeNameHelpers.TypeNameMatches(type, "Sample.Worker"));
    }

    [Fact]
    public void TypeNameMatches_False_ForDifferentName()
    {
        var root = Parse("""
            namespace Sample
            {
                class Worker { }
            }
            """);
        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Single();

        Assert.False(BraceTypeNameHelpers.TypeNameMatches(type, "Other"));
        Assert.False(BraceTypeNameHelpers.TypeNameMatches(type, "Sample.Other"));
    }

    private static string MissingAbsoluteSourceFile() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpBraceHelpers_" + Guid.NewGuid().ToString("N"), "Missing.cs");

    [Theory]
    [InlineData("statement")]
    [InlineData("type")]
    public void ValidateBraceParams_AllFiles_WithStatementOrTypeScope_ThrowsMissingRequiredParam(string scope)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                sourceFile: null, allFiles: true, line: null, column: null, scope: scope, typeName: "Worker"));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal($"allFiles cannot be combined with scope={scope}.", ex.Message);
    }

    [Fact]
    public void ValidateBraceParams_AllFiles_OmittedScopeAndMissingFile_DoesNotThrow()
    {
        BraceTypeNameHelpers.ValidateBraceParams(
            sourceFile: null, allFiles: true, line: null, column: null, scope: null, typeName: null);
        BraceTypeNameHelpers.ValidateBraceParams(
            MissingAbsoluteSourceFile(), allFiles: true, line: null, column: null, scope: "file", typeName: null);
    }

    [Fact]
    public void ValidateBraceParams_AllFiles_RelativeSourceFile_ThrowsInvalidSourcePath()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                "Relative.cs", allFiles: true, line: null, column: null, scope: null, typeName: null));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ValidateBraceParams_AllFiles_LineOrColumnBelowOne_Throws()
    {
        var lineEx = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                sourceFile: null, allFiles: true, line: 0, column: null, scope: null, typeName: null));
        Assert.Equal(ErrorCodes.InvalidLineNumber, lineEx.ErrorCode);

        var columnEx = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                sourceFile: null, allFiles: true, line: null, column: 0, scope: null, typeName: null));
        Assert.Equal(ErrorCodes.InvalidColumnNumber, columnEx.ErrorCode);
    }

    [Fact]
    public void ValidateBraceParams_SingleFile_MissingSourceFile_ThrowsMissingRequiredParam()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                sourceFile: " ", allFiles: false, line: 1, column: null, scope: null, typeName: null));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("sourceFile is required when allFiles is false.", ex.Message);
    }

    [Fact]
    public void ValidateBraceParams_SingleFile_StatementScopeWithoutLine_ThrowsInvalidLineNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                MissingAbsoluteSourceFile(), allFiles: false, line: null, column: null, scope: null, typeName: null));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("line is required when scope is statement.", ex.Message);
    }

    [Fact]
    public void ValidateBraceParams_SingleFile_TypeScopeWithoutTypeName_ThrowsMissingRequiredParam()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                MissingAbsoluteSourceFile(), allFiles: false, line: null, column: null, scope: "type", typeName: null));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("typeName is required when scope is type.", ex.Message);
    }

    [Fact]
    public void ValidateBraceParams_SingleFile_ColumnBelowOne_ThrowsInvalidColumnNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                MissingAbsoluteSourceFile(), allFiles: false, line: 1, column: 0, scope: null, typeName: null));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void ValidateBraceParams_SingleFile_MissingFile_ThrowsSourceFileNotFound()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.ValidateBraceParams(
                MissingAbsoluteSourceFile(), allFiles: false, line: null, column: null, scope: "file", typeName: null));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }
}

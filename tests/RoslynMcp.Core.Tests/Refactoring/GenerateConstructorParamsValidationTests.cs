using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for GenerateConstructorParams validation.
/// </summary>
public class GenerateConstructorParamsValidationTests
{
    /// <summary>
    /// Returns a platform-appropriate absolute path for test purposes.
    /// On Windows: C:\test\file.cs, on Unix: /test/file.cs
    /// </summary>
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";
    [Fact]
    public void ValidateParams_MissingSourceFile_ThrowsException()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = "",
            TypeName = "MyClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MissingTypeName_ThrowsException()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = ""
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_RelativePath_ThrowsException()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = "file.cs",
            TypeName = "MyClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_ValidParams_ThrowsSourceFileNotFound()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Members = new List<string> { "Field1", "Field2" },
            AddNullChecks = true
        };

        // Should only fail on file not found
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void ValidateParams_InvalidLine_ThrowsInvalidLineNumber()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Line = 0
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_NegativeLine_ThrowsInvalidLineNumber()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Line = -1
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void IncludeProperties_DefaultsToTrue()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.True(@params.IncludeProperties);
    }

    [Fact]
    public void IncludeInheritedMembers_DefaultsToFalse()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.False(@params.IncludeInheritedMembers);
    }

    [Fact]
    public void ReplaceExisting_DefaultsToFalse()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.False(@params.ReplaceExisting);
    }

    [Fact]
    public void Visibility_DefaultsToNull()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.Null(@params.Visibility);
    }

    [Fact]
    public void CopyConstructor_DefaultsToFalse()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.False(@params.CopyConstructor);
    }

    [Fact]
    public void ClassBaseCopy_DefaultsToFalse()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.False(@params.ClassBaseCopy);
    }

    [Fact]
    public void CallBase_DefaultsToFalse()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass"
        };

        Assert.False(@params.CallBase);
    }

    [Fact]
    public void ValidateParams_ClassBaseCopyWithoutCopyConstructor_ThrowsClassBaseCopyRequiresCopyConstructor()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            ClassBaseCopy = true,
            CopyConstructor = false
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.ClassBaseCopyRequiresCopyConstructor, ex.ErrorCode);
        Assert.Contains("copyConstructor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateParams_CallBaseWithCopyConstructor_ThrowsCallBaseConflictsWithCopyConstructor()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            CallBase = true,
            CopyConstructor = true
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.CallBaseConflictsWithCopyConstructor, ex.ErrorCode);
        Assert.Contains("copyConstructor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateParams_InvalidVisibility_ThrowsInvalidVisibility()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Visibility = "secret"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("public")]
    [InlineData("private")]
    [InlineData("protected")]
    [InlineData("internal")]
    [InlineData("protected internal")]
    [InlineData("private protected")]
    [InlineData("Public")]
    public void ValidateParams_OmittedOrValidVisibility_DoesNotThrowForVisibility(string? visibility)
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Visibility = visibility
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_NullMembers_AcceptsNull()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Members = null
        };

        // Should only fail on file not found, not on null members
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateConstructorOperation.Validate(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

}

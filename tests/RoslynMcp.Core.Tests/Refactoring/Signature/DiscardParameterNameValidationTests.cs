using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// #3298: add_parameter (parameterName) and change_signature (an added or renamed
/// parameter's name) refuse "_". A parameter named "_" captures every discard in the
/// method body (_ = Log(7) / out _), so the edit used to report success while the
/// build broke (CS0029 / CS1503) or the discards silently wrote to the parameter.
/// </summary>
public class DiscardParameterNameValidationTests
{
    [Theory]
    [InlineData("_")]
    [InlineData("@_")]
    public void AddParameter_ParameterNameIsDiscard_ThrowsInvalidSymbolName(string parameterName)
    {
        WithTempFile(file =>
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                AddParameterOperation.Validate(new AddParameterParams
                {
                    SourceFile = file,
                    MethodName = "Work",
                    ParameterName = parameterName,
                    ParameterType = "string",
                    DefaultValue = "\"x\""
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
            if (parameterName == "_")
                Assert.Contains("discard", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void AddParameter_AllFilesParameterNameIsDiscard_ThrowsInvalidSymbolName()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.Validate(new AddParameterParams
            {
                AllFiles = true,
                ParameterName = "_",
                ParameterType = "int"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
    }

    [Theory]
    [InlineData("__")]
    [InlineData("_tag")]
    public void AddParameter_ParameterNameWithLeadingUnderscore_IsAccepted(string parameterName)
    {
        WithTempFile(file =>
            AddParameterOperation.Validate(new AddParameterParams
            {
                SourceFile = file,
                MethodName = "Work",
                ParameterName = parameterName,
                ParameterType = "int"
            }));
    }

    [Theory]
    [InlineData(null, "_")]
    [InlineData(null, "@_")]
    [InlineData("s", "_")]
    public void ChangeSignature_AddedOrRenamedParameterIsDiscard_ThrowsInvalidSymbolName(string? originalName, string name)
    {
        WithTempFile(file =>
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ChangeSignatureOperation.Validate(new ChangeSignatureParams
                {
                    SourceFile = file,
                    MethodName = "Work",
                    Parameters =
                    [
                        new() { OriginalName = "x", Name = "x" },
                        new() { OriginalName = originalName, Name = name, Type = originalName == null ? "int" : null }
                    ]
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
            if (name == "_")
                Assert.Contains("discard", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ChangeSignature_AllFilesAddedParameterIsDiscard_ThrowsInvalidSymbolName()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                AllFiles = true,
                Parameters = [new() { Name = "_", Type = "int" }]
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
    }

    [Fact]
    public void ChangeSignature_KeepingExistingDiscardNamedParameter_IsAccepted()
    {
        // An existing parameter already named "_" already owns the name; keeping it
        // (or retyping it) does not change what the body's "_" binds to.
        WithTempFile(file =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = file,
                MethodName = "Work",
                Parameters =
                [
                    new() { OriginalName = "_", Name = "_" },
                    new() { Name = "flag", Type = "bool" }
                ]
            }));
    }

    [Theory]
    [InlineData("__")]
    [InlineData("_tag")]
    public void ChangeSignature_ParameterNameWithLeadingUnderscore_IsAccepted(string name)
    {
        WithTempFile(file =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = file,
                MethodName = "Work",
                Parameters =
                [
                    new() { OriginalName = "x", Name = "x" },
                    new() { Name = name, Type = "int" }
                ]
            }));
    }

    private static void WithTempFile(Action<string> body)
    {
        var file = Path.Combine(Path.GetTempPath(), $"RoslynMcpDiscardParam_{Guid.NewGuid():N}.cs");
        File.WriteAllText(file, "// test");
        try
        {
            body(file);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

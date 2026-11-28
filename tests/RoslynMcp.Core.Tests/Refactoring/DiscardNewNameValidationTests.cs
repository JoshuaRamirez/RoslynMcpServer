using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// #3297: encapsulate_field (propertyName), introduce_field (fieldName) and
/// extract_variable (variableName) refuse "_". A property, field or local named "_"
/// captures every existing discard in scope (_ = Log(x) / out _), so the edit used to
/// report success while silently writing to the new symbol (or failing with CS0206).
/// </summary>
public class DiscardNewNameValidationTests
{
    [Theory]
    [InlineData("_")]
    [InlineData("@_")]
    public void EncapsulateField_PropertyNameIsDiscard_ThrowsInvalidSymbolName(string propertyName)
    {
        WithTempFile(file =>
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                EncapsulateFieldOperation.Validate(new EncapsulateFieldParams
                {
                    SourceFile = file,
                    FieldName = "total",
                    PropertyName = propertyName
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
            Assert.Equal(
                "'_' is not a valid property name: existing discards (_ = ... / out _) would bind to the new property.",
                ex.Message);
        });
    }

    [Theory]
    [InlineData("__")]
    [InlineData("_Total")]
    public void EncapsulateField_PropertyNameWithLeadingUnderscore_IsAccepted(string propertyName)
    {
        WithTempFile(file =>
            EncapsulateFieldOperation.Validate(new EncapsulateFieldParams
            {
                SourceFile = file,
                FieldName = "total",
                PropertyName = propertyName
            }));
    }

    [Theory]
    [InlineData("_")]
    [InlineData("@_")]
    public void IntroduceField_FieldNameIsDiscard_ThrowsInvalidSymbolName(string fieldName)
    {
        WithTempFile(file =>
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                IntroduceFieldOperation.Validate(new IntroduceFieldParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 1,
                    EndColumn = 5,
                    FieldName = fieldName
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
            Assert.Equal(
                fieldName == "_"
                    ? "'_' is not a valid field name: existing discards (_ = ... / out _) would bind to the new field."
                    : "Invalid field name: @_",
                ex.Message);
        });
    }

    [Theory]
    [InlineData("__")]
    [InlineData("_value")]
    public void IntroduceField_FieldNameWithLeadingUnderscore_IsAccepted(string fieldName)
    {
        WithTempFile(file =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = file,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 5,
                FieldName = fieldName
            }));
    }

    [Theory]
    [InlineData("_")]
    [InlineData("@_")]
    public void ExtractVariable_VariableNameIsDiscard_ThrowsInvalidSymbolName(string variableName)
    {
        WithTempFile(file =>
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractVariableOperation.Validate(new ExtractVariableParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 1,
                    EndColumn = 5,
                    VariableName = variableName
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
            Assert.Equal(
                variableName == "_"
                    ? "'_' is not a valid variable name: existing discards (_ = ... / out _) would bind to the new local."
                    : "Invalid variable name: @_",
                ex.Message);
        });
    }

    [Theory]
    [InlineData("__")]
    [InlineData("_scaled")]
    public void ExtractVariable_VariableNameWithLeadingUnderscore_IsAccepted(string variableName)
    {
        WithTempFile(file =>
            ExtractVariableOperation.Validate(new ExtractVariableParams
            {
                SourceFile = file,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 5,
                VariableName = variableName
            }));
    }

    private static void WithTempFile(Action<string> body)
    {
        var file = Path.Combine(Path.GetTempPath(), $"RoslynMcpDiscardName_{Guid.NewGuid():N}.cs");
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

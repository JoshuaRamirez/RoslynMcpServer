using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Validates the shared line / column / symbolName target parameters of the position-or-name query
/// operations <c>find_references</c>, <c>find_implementations</c>, <c>find_callers</c>,
/// <c>get_type_hierarchy</c>, <c>get_symbol_info</c>, and <c>go_to_definition</c>.
/// </summary>
internal static class SymbolTargetValidator
{
    /// <summary>
    /// Throws, in this order: <see cref="ErrorCodes.MissingRequiredParam"/> when neither
    /// <paramref name="line"/> nor a non-blank <paramref name="symbolName"/> is provided;
    /// <see cref="ErrorCodes.InvalidLineNumber"/> when <paramref name="line"/> is set and &lt; 1;
    /// <see cref="ErrorCodes.InvalidColumnNumber"/> when <paramref name="column"/> is set and &lt; 1.
    /// A column without a line still requires symbolName.
    /// </summary>
    public static void Validate(int? line, int? column, string? symbolName)
    {
        if (!line.HasValue && string.IsNullOrWhiteSpace(symbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (line.HasValue && line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (column.HasValue && column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
    }
}

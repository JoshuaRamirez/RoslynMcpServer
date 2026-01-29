namespace RoslynMcp.Contracts.Errors;

/// <summary>
/// Represents an error that occurred during a refactoring operation.
/// </summary>
public sealed class RefactoringError
{
    /// <summary>
    /// Machine-readable error code from <see cref="ErrorCodes"/>.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable error description.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Additional context-specific information.
    /// </summary>
    public Dictionary<string, object>? Details { get; init; }

    /// <summary>
    /// Possible remediation actions.
    /// </summary>
    public List<string>? Suggestions { get; init; }

    /// <summary>
    /// Creates an error with the specified code and message.
    /// </summary>
    public static RefactoringError Create(string code, string message) =>
        new() { Code = code, Message = message };

    /// <summary>
    /// Creates an error with details and suggestions.
    /// </summary>
    public static RefactoringError Create(
        string code,
        string message,
        Dictionary<string, object>? details = null,
        List<string>? suggestions = null) =>
        new()
        {
            Code = code,
            Message = message,
            Details = details,
            Suggestions = suggestions
        };
}

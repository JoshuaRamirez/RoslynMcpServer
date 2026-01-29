using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Exception thrown during refactoring operations.
/// Contains structured error information for MCP responses.
/// </summary>
public sealed class RefactoringException : Exception
{
    /// <summary>
    /// Machine-readable error code from <see cref="ErrorCodes"/>.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Additional context-specific information.
    /// </summary>
    public Dictionary<string, object>? Details { get; }

    /// <summary>
    /// Possible remediation actions.
    /// </summary>
    public List<string>? Suggestions { get; }

    /// <summary>
    /// Creates a new refactoring exception.
    /// </summary>
    /// <param name="errorCode">Error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable message.</param>
    public RefactoringException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Creates a new refactoring exception with details.
    /// </summary>
    /// <param name="errorCode">Error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="details">Additional context.</param>
    /// <param name="suggestions">Remediation suggestions.</param>
    public RefactoringException(
        string errorCode,
        string message,
        Dictionary<string, object>? details = null,
        List<string>? suggestions = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Details = details;
        Suggestions = suggestions;
    }

    /// <summary>
    /// Creates a new refactoring exception wrapping an inner exception.
    /// </summary>
    /// <param name="errorCode">Error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="innerException">Inner exception.</param>
    public RefactoringException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Converts this exception to a <see cref="RefactoringError"/>.
    /// </summary>
    public RefactoringError ToError() => new()
    {
        Code = ErrorCode,
        Message = Message,
        Details = Details,
        Suggestions = Suggestions
    };
}

namespace RoslynMcp.Core.Workspace;

internal enum WorkspaceFailureCategory
{
    UnrecognizedProject,
    OtherFailure
}

internal static class WorkspaceLoadFailureClassifier
{
    private const string _unsupportedProjectPrefix = "Cannot open project '";
    private const string _unsupportedProjectMarker = "' because the file extension '";
    private const string _unsupportedProjectSuffix = "' is not associated with a language.";
    private const string _invalidProjectFilePathPrefix = "Invalid project file path: '";
    private const string _projectFileNotFoundPrefix = "Project file not found: '";

    public static WorkspaceFailureCategory Classify(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return WorkspaceFailureCategory.OtherFailure;
        }

        if (message.StartsWith(_unsupportedProjectPrefix, StringComparison.Ordinal) &&
            message.Contains(_unsupportedProjectMarker, StringComparison.Ordinal) &&
            message.EndsWith(_unsupportedProjectSuffix, StringComparison.Ordinal))
        {
            return WorkspaceFailureCategory.UnrecognizedProject;
        }

        if (message.StartsWith(_invalidProjectFilePathPrefix, StringComparison.Ordinal) &&
            message.EndsWith('\''))
        {
            return WorkspaceFailureCategory.UnrecognizedProject;
        }

        if (message.StartsWith(_projectFileNotFoundPrefix, StringComparison.Ordinal) &&
            message.EndsWith('\''))
        {
            return WorkspaceFailureCategory.UnrecognizedProject;
        }

        return WorkspaceFailureCategory.OtherFailure;
    }
}

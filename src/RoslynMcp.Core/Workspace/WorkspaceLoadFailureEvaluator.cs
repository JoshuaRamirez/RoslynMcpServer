using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Workspace;

internal sealed class WorkspaceLoadEvaluationResult
{
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public bool HasErrors => Errors.Count > 0;
}

internal static class WorkspaceLoadFailureEvaluator
{
    private const string _noSupportedProjectsMessage = "No supported projects were loaded.";

    public static WorkspaceLoadEvaluationResult Evaluate(
        IEnumerable<WorkspaceDiagnostic> diagnostics,
        WorkspaceLoadOptions options,
        int projectCount)
    {
        List<string> errors = [];
        List<string> warnings = [];

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
            {
                warnings.Add(diagnostic.Message);
                continue;
            }

            var category = WorkspaceLoadFailureClassifier.Classify(diagnostic.Message);
            if (category == WorkspaceFailureCategory.UnrecognizedProject && options.SkipUnrecognizedProjects)
            {
                warnings.Add(diagnostic.Message);
                continue;
            }

            errors.Add(diagnostic.Message);
        }

        if (projectCount == 0)
        {
            errors.Add(_noSupportedProjectsMessage);
        }

        return new WorkspaceLoadEvaluationResult
        {
            Errors = errors,
            Warnings = warnings
        };
    }
}

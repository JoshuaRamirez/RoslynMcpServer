namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Options that control workspace loading behavior.
/// </summary>
public sealed class WorkspaceLoadOptions
{
    /// <summary>
    /// Default workspace load options.
    /// </summary>
    public static WorkspaceLoadOptions Default { get; } = new();

    /// <summary>
    /// Whether Roslyn should skip unrecognized projects during solution load.
    /// </summary>
    public bool SkipUnrecognizedProjects { get; init; }
}

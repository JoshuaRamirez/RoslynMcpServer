namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Represents the lifecycle state of a workspace.
/// </summary>
public enum WorkspaceState
{
    /// <summary>Initial state; no solution loaded.</summary>
    Unloaded,

    /// <summary>Solution load in progress.</summary>
    Loading,

    /// <summary>Solution loaded; available for operations.</summary>
    Ready,

    /// <summary>Refactoring operation in progress.</summary>
    Operating,

    /// <summary>Load or operation failed.</summary>
    Error,

    /// <summary>Workspace released.</summary>
    Disposed
}

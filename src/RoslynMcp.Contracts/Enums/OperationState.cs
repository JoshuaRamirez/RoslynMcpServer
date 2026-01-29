namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Represents the execution state of a refactoring operation.
/// </summary>
public enum OperationState
{
    /// <summary>Request received, not yet started.</summary>
    Pending,

    /// <summary>Input validation in progress.</summary>
    Validating,

    /// <summary>Finding symbol and references.</summary>
    Resolving,

    /// <summary>Calculating document changes.</summary>
    Computing,

    /// <summary>Preview mode; changes ready for review.</summary>
    Previewing,

    /// <summary>Writing changes to workspace model.</summary>
    Applying,

    /// <summary>Persisting changes to filesystem.</summary>
    Committing,

    /// <summary>Operation finished successfully.</summary>
    Completed,

    /// <summary>Operation terminated with error.</summary>
    Failed,

    /// <summary>Operation cancelled by user.</summary>
    Cancelled
}

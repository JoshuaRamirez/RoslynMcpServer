using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Server.Tests.TestHelpers;

/// <summary>
/// Test double for <see cref="IWorkspaceProvider"/> that fails loudly if workspace
/// creation is ever attempted.
/// </summary>
/// <remarks>
/// Tool implementations do not validate individual arguments before calling
/// <see cref="IWorkspaceProvider.CreateContextAsync"/>; per-field validation happens
/// inside each refactoring operation's ValidateParams, which requires a real
/// <see cref="WorkspaceContext"/> and is covered separately in RoslynMcp.Core.Tests.
/// Using this provider (instead of a bare <c>null!</c>) ensures argument-validation
/// tests fail with a clear, unambiguous exception message if a code change causes
/// workspace creation to actually be attempted, rather than incidentally passing
/// because a <see cref="System.NullReferenceException"/> happened to be caught by
/// the tool's generic error handler.
/// </remarks>
public sealed class ThrowingWorkspaceProvider : IWorkspaceProvider
{
    public Task<WorkspaceContext> CreateContextAsync(
        string projectOrSolutionPath,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "Workspace creation should not have been attempted in this test.");
    }

    public EnvironmentDiagnostics CheckEnvironment()
    {
        throw new InvalidOperationException(
            "Workspace creation should not have been attempted in this test.");
    }
}

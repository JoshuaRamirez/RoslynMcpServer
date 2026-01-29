using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Scoped workspace session. Encapsulates MSBuildWorkspace lifecycle.
/// Must be disposed after refactoring operation completes.
/// </summary>
/// <remarks>
/// This class is not thread-safe for concurrent commit operations.
/// Use separate WorkspaceContext instances for parallel operations,
/// or ensure commits are serialized externally.
/// </remarks>
public sealed class WorkspaceContext : IDisposable
{
    private readonly MSBuildWorkspace _workspace;
    private readonly IFileWriter _fileWriter;
    private readonly SemaphoreSlim _commitLock = new(1, 1);
    private Solution _solution;
    private bool _disposed;

    /// <summary>
    /// Current solution snapshot.
    /// </summary>
    public Solution Solution => _solution;

    /// <summary>
    /// The underlying Roslyn workspace.
    /// </summary>
    public Microsoft.CodeAnalysis.Workspace Workspace => _workspace;

    /// <summary>
    /// Path to the loaded solution or project.
    /// </summary>
    public string LoadedPath { get; }

    /// <summary>
    /// Current workspace state.
    /// </summary>
    public WorkspaceState State { get; private set; }

    internal WorkspaceContext(
        MSBuildWorkspace workspace,
        Solution solution,
        string loadedPath,
        IFileWriter? fileWriter = null)
    {
        _workspace = workspace;
        _solution = solution;
        _fileWriter = fileWriter ?? new AtomicFileWriter();
        LoadedPath = loadedPath;
        State = WorkspaceState.Ready;
    }

    /// <summary>
    /// Creates a symbol resolver for this workspace.
    /// </summary>
    public TypeSymbolResolver CreateSymbolResolver() => new(this);

    /// <summary>
    /// Creates a reference tracker for this workspace.
    /// </summary>
    public ReferenceTracker CreateReferenceTracker() => new(this);

    /// <summary>
    /// Gets a document by its file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <returns>Document if found, null otherwise.</returns>
    public Document? GetDocumentByPath(string filePath)
    {
        var normalizedPath = PathResolver.NormalizePath(filePath);
        return _solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => PathResolver.NormalizePath(d.FilePath ?? "") == normalizedPath);
    }

    /// <summary>
    /// Updates the solution with a new snapshot.
    /// </summary>
    /// <param name="newSolution">New solution snapshot.</param>
    public void UpdateSolution(Solution newSolution)
    {
        _solution = newSolution;
    }

    /// <summary>
    /// Commits all pending changes to the filesystem.
    /// </summary>
    /// <param name="newSolution">Solution with changes to commit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of files that were written.</returns>
    /// <remarks>
    /// This method uses a semaphore to prevent race conditions when multiple
    /// commit operations are attempted concurrently on the same workspace context.
    /// Files are written sequentially to avoid file locking issues.
    /// </remarks>
    public async Task<CommitResult> CommitChangesAsync(
        Solution newSolution,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Acquire lock to prevent concurrent commits
        await _commitLock.WaitAsync(cancellationToken);

        var filesModified = new List<string>();
        var filesCreated = new List<string>();
        var filesDeleted = new List<string>();

        try
        {
            State = WorkspaceState.Operating;

            var changes = newSolution.GetChanges(_solution);

            // Collect all file operations first, then execute sequentially
            // This prevents interleaved writes to the same file from different documents
            var fileOperations = new List<(string FilePath, Func<Task> Operation, string Category)>();

            foreach (var projectChanges in changes.GetProjectChanges())
            {
                // Handle added documents
                foreach (var docId in projectChanges.GetAddedDocuments())
                {
                    var doc = newSolution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    fileOperations.Add((filePath, async () =>
                    {
                        var text = await doc.GetTextAsync(cancellationToken);
                        await _fileWriter.WriteAsync(filePath, text.ToString(), cancellationToken);
                    }, "created"));
                    filesCreated.Add(filePath);
                }

                // Handle changed documents
                foreach (var docId in projectChanges.GetChangedDocuments())
                {
                    var doc = newSolution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    fileOperations.Add((filePath, async () =>
                    {
                        var text = await doc.GetTextAsync(cancellationToken);
                        await _fileWriter.WriteAsync(filePath, text.ToString(), cancellationToken);
                    }, "modified"));
                    filesModified.Add(filePath);
                }

                // Handle removed documents
                foreach (var docId in projectChanges.GetRemovedDocuments())
                {
                    var doc = _solution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    fileOperations.Add((filePath, () =>
                    {
                        _fileWriter.Delete(filePath);
                        return Task.CompletedTask;
                    }, "deleted"));
                    filesDeleted.Add(filePath);
                }
            }

            // Sort operations by file path to ensure consistent ordering
            // and prevent potential deadlocks with external file locks
            fileOperations.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase));

            // Execute file operations sequentially to prevent race conditions
            foreach (var (_, operation, _) in fileOperations)
            {
                await operation();
            }

            _solution = newSolution;
            State = WorkspaceState.Ready;

            return new CommitResult
            {
                Success = true,
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted
            };
        }
        catch (Exception ex)
        {
            State = WorkspaceState.Error;
            return new CommitResult
            {
                Success = false,
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted,
                Error = ex.Message
            };
        }
        finally
        {
            _commitLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        State = WorkspaceState.Disposed;
        _commitLock.Dispose();
        _workspace.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// Result of committing changes to the filesystem.
/// </summary>
public sealed class CommitResult
{
    /// <summary>
    /// Whether the commit succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Files that were modified.
    /// </summary>
    public required IReadOnlyList<string> FilesModified { get; init; }

    /// <summary>
    /// Files that were created.
    /// </summary>
    public required IReadOnlyList<string> FilesCreated { get; init; }

    /// <summary>
    /// Files that were deleted.
    /// </summary>
    public required IReadOnlyList<string> FilesDeleted { get; init; }

    /// <summary>
    /// Error message if commit failed.
    /// </summary>
    public string? Error { get; init; }
}

using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Represents a warm, persistent in-memory Roslyn <see cref="Solution"/> session with incremental
/// <see cref="Solution.WithDocumentText(DocumentId, SourceText, PreservationMode)"/> updates and atomic commit locking.
/// </summary>
public sealed class WarmSolutionSession : IDisposable
{
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly IFileWriter _fileWriter;
    private readonly WorkspaceContext? _ownedContext;
    private readonly FileSystemWatcher? _watcher;
    private Solution _currentSolution;
    private long _revision;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="WarmSolutionSession"/>.
    /// </summary>
    /// <param name="solutionKey">Canonical solution or project path key.</param>
    /// <param name="initialSolution">Initial loaded Roslyn solution snapshot.</param>
    /// <param name="fileWriter">Optional atomic file writer for disk commits.</param>
    /// <param name="ownedContext">Optional underlying <see cref="WorkspaceContext"/> to dispose with the session.</param>
    /// <param name="watchDirectory">Optional root directory to watch for external <c>.cs</c> file changes.</param>
    public WarmSolutionSession(
        string solutionKey,
        Solution initialSolution,
        IFileWriter? fileWriter = null,
        WorkspaceContext? ownedContext = null,
        string? watchDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionKey);
        ArgumentNullException.ThrowIfNull(initialSolution);

        SolutionKey = solutionKey;
        _currentSolution = initialSolution;
        _fileWriter = fileWriter ?? new AtomicFileWriter();
        _ownedContext = ownedContext;
        _revision = 1;

        if (!string.IsNullOrWhiteSpace(watchDirectory) && Directory.Exists(watchDirectory))
        {
            _watcher = new FileSystemWatcher(watchDirectory, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _watcher.Changed += OnExternalFileChanged;
            _watcher.Created += OnExternalFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>
    /// Canonical solution key.
    /// </summary>
    public string SolutionKey { get; }

    /// <summary>
    /// Current immutable <see cref="Solution"/> snapshot held in warm memory.
    /// </summary>
    public Solution CurrentSolution => Volatile.Read(ref _currentSolution);

    /// <summary>
    /// Monotonically increasing revision counter bumped on every incremental update or committed transaction.
    /// </summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// Incrementally updates a document's source text in memory using <see cref="Solution.WithDocumentText(DocumentId, SourceText, PreservationMode)"/>
    /// without reloading the MSBuild workspace.
    /// </summary>
    /// <param name="documentPath">Absolute or relative file path of the document.</param>
    /// <param name="newSourceText">Updated C# source text.</param>
    /// <returns><c>true</c> if at least one document in the solution was updated; otherwise <c>false</c>.</returns>
    public bool InvalidateOrUpdateDocument(string documentPath, string newSourceText)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(newSourceText);

        _sessionLock.Wait();
        try
        {
            var matchingIds = FindDocumentIds(_currentSolution, documentPath);
            if (matchingIds.Count == 0)
            {
                return false;
            }

            var sourceText = SourceText.From(newSourceText, Encoding.UTF8);
            var updated = _currentSolution;
            foreach (var docId in matchingIds)
            {
                updated = updated.WithDocumentText(docId, sourceText, PreservationMode.PreserveIdentity);
            }

            _currentSolution = updated;
            _ownedContext?.UpdateSolution(updated);
            Interlocked.Increment(ref _revision);
            return true;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Acquires the exclusive session lock, runs a transactional fold or commit function, and releases the lock.
    /// </summary>
    /// <typeparam name="TResult">Return type of <paramref name="work"/>.</typeparam>
    /// <param name="work">Asynchronous callback receiving the current warm <see cref="WarmSolutionSession"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result produced by <paramref name="work"/>.</returns>
    public async Task<TResult> ExecuteSerializedAsync<TResult>(
        Func<WarmSolutionSession, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(this, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Atomically commits <paramref name="newSolution"/> as the new warm session state and optionally persists
    /// all added, modified, and removed documents to disk.
    /// </summary>
    /// <param name="newSolution">The post-transaction solution snapshot to commit.</param>
    /// <param name="persistToDisk">Whether to write changed documents to the filesystem via <see cref="IFileWriter"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CommitAuthorizedSolutionAsync(
        Solution newSolution,
        bool persistToDisk,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(newSolution);

        var wasWatching = _watcher?.EnableRaisingEvents ?? false;
        if (_watcher is not null && wasWatching)
        {
            _watcher.EnableRaisingEvents = false;
        }

        try
        {
            if (persistToDisk)
            {
                var changes = newSolution.GetChanges(_currentSolution);
                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var projectChanges in changes.GetProjectChanges())
                {
                    foreach (var docId in projectChanges.GetAddedDocuments().Concat(projectChanges.GetChangedDocuments()))
                    {
                        var doc = newSolution.GetDocument(docId);
                        if (doc?.FilePath is { } filePath && Path.IsPathRooted(filePath) && writtenPaths.Add(filePath))
                        {
                            var parentDir = Path.GetDirectoryName(filePath);
                            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                            {
                                var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                                await _fileWriter.WriteAsync(filePath, text.ToString(), cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    foreach (var docId in projectChanges.GetRemovedDocuments())
                    {
                        var oldDoc = _currentSolution.GetDocument(docId);
                        if (oldDoc?.FilePath is { } filePath && File.Exists(filePath) && writtenPaths.Add(filePath))
                        {
                            _fileWriter.Delete(filePath);
                        }
                    }
                }
            }

            if (_ownedContext is not null)
            {
                _ownedContext.UpdateSolution(newSolution);
            }
            else if (newSolution.Workspace is not null && newSolution.Workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
            {
                _ = newSolution.Workspace.TryApplyChanges(newSolution);
            }

            _currentSolution = newSolution;
            Interlocked.Increment(ref _revision);
        }
        finally
        {
            if (_watcher is not null && wasWatching)
            {
                _watcher.EnableRaisingEvents = true;
            }
        }
    }

    private static List<DocumentId> FindDocumentIds(Solution solution, string documentPath)
    {
        var normalizedTarget = documentPath.Replace('\\', '/').TrimEnd('/');
        var targetKey = Path.IsPathRooted(documentPath) && File.Exists(documentPath)
            ? PathResolver.GetPathComparisonKey(documentPath).Replace('\\', '/')
            : normalizedTarget;

        var matchingIds = new List<DocumentId>();
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                var rawPath = doc.FilePath ?? doc.Name;
                var normalizedDoc = rawPath.Replace('\\', '/');
                if (string.Equals(normalizedDoc, targetKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedDoc, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                    normalizedDoc.EndsWith("/" + normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(doc.Name, documentPath, StringComparison.OrdinalIgnoreCase))
                {
                    matchingIds.Add(doc.Id);
                }
            }
        }

        return matchingIds;
    }

    private void OnExternalFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed || string.IsNullOrWhiteSpace(e.FullPath) || !File.Exists(e.FullPath))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(e.FullPath);
            InvalidateOrUpdateDocument(e.FullPath, text);
        }
        catch (IOException)
        {
            // Ignore transient file lock races during external edits.
        }
        catch (ObjectDisposedException)
        {
            // Session disposed while event was in flight.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _ownedContext?.Dispose();
        _sessionLock.Dispose();
    }
}

/// <summary>
/// Manages persistent in-memory <see cref="WarmSolutionSession"/> instances keyed by canonical solution path,
/// eliminating cold MSBuild workspace reloads across sequential MCP tool calls and transactions.
/// </summary>
public sealed class SolutionSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, WarmSolutionSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _creationLock = new(1, 1);
    private readonly IWorkspaceProvider? _workspaceProvider;
    private readonly IFileWriter _fileWriter;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="SolutionSessionManager"/>.
    /// </summary>
    /// <param name="workspaceProvider">Optional workspace provider for loading <c>.sln</c> or <c>.csproj</c> paths from disk.</param>
    /// <param name="fileWriter">Optional file writer used when committing changes to disk.</param>
    public SolutionSessionManager(
        IWorkspaceProvider? workspaceProvider = null,
        IFileWriter? fileWriter = null)
    {
        _workspaceProvider = workspaceProvider;
        _fileWriter = fileWriter ?? new AtomicFileWriter();
    }

    /// <summary>
    /// Number of warm solution sessions currently cached in memory.
    /// </summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>
    /// Registers or replaces an in-memory <see cref="WarmSolutionSession"/> for <paramref name="solutionPath"/>.
    /// </summary>
    /// <param name="solutionPath">Solution path or identifier.</param>
    /// <param name="initialSolution">Initial Roslyn solution snapshot.</param>
    /// <param name="watchDirectory">Optional directory to watch with <see cref="FileSystemWatcher"/>.</param>
    /// <returns>The registered <see cref="WarmSolutionSession"/>.</returns>
    public WarmSolutionSession RegisterInMemorySession(
        string solutionPath,
        Solution initialSolution,
        string? watchDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        ArgumentNullException.ThrowIfNull(initialSolution);

        var key = NormalizeSessionKey(solutionPath);
        var session = new WarmSolutionSession(key, initialSolution, _fileWriter, ownedContext: null, watchDirectory: watchDirectory);

        if (_sessions.TryRemove(key, out var existing))
        {
            existing.Dispose();
        }

        _sessions[key] = session;
        return session;
    }

    /// <summary>
    /// Gets an existing warm <see cref="WarmSolutionSession"/> or loads it once via <see cref="IWorkspaceProvider"/>.
    /// </summary>
    /// <param name="solutionPath">Path to the <c>.sln</c>, <c>.slnx</c>, or <c>.csproj</c> file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached warm <see cref="WarmSolutionSession"/>.</returns>
    public async Task<WarmSolutionSession> GetOrLoadSessionAsync(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);

        var key = NormalizeSessionKey(solutionPath);
        if (_sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        await _creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(key, out existing))
            {
                return existing;
            }

            if (_workspaceProvider is null)
            {
                throw new InvalidOperationException(
                    $"No warm session is registered for '{solutionPath}' and no IWorkspaceProvider was supplied.");
            }

            var context = await _workspaceProvider.CreateContextAsync(solutionPath, cancellationToken).ConfigureAwait(false);
            var watchDir = Path.GetDirectoryName(context.LoadedPath);
            var session = new WarmSolutionSession(
                key,
                context.Solution,
                _fileWriter,
                ownedContext: context,
                watchDirectory: watchDir);

            _sessions[key] = session;
            return session;
        }
        finally
        {
            _creationLock.Release();
        }
    }

    /// <summary>
    /// Incrementally updates a document in a cached session using <see cref="Solution.WithDocumentText(DocumentId, SourceText, PreservationMode)"/>.
    /// </summary>
    /// <param name="solutionPath">Solution path key.</param>
    /// <param name="documentPath">Document file path.</param>
    /// <param name="newSourceText">Updated C# source code.</param>
    /// <returns><c>true</c> if the warm session was found and the document was updated; otherwise <c>false</c>.</returns>
    public bool InvalidateOrUpdateDocument(string solutionPath, string documentPath, string newSourceText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = NormalizeSessionKey(solutionPath);
        return _sessions.TryGetValue(key, out var session) &&
               session.InvalidateOrUpdateDocument(documentPath, newSourceText);
    }

    /// <summary>
    /// Evicts and disposes a cached session if present.
    /// </summary>
    /// <param name="solutionPath">Solution path key.</param>
    /// <returns><c>true</c> if removed; otherwise <c>false</c>.</returns>
    public bool EvictSession(string solutionPath)
    {
        var key = NormalizeSessionKey(solutionPath);
        if (_sessions.TryRemove(key, out var session))
        {
            session.Dispose();
            return true;
        }

        return false;
    }

    private static string NormalizeSessionKey(string solutionPath)
    {
        if (Path.IsPathRooted(solutionPath) && (File.Exists(solutionPath) || Directory.Exists(solutionPath)))
        {
            return PathResolver.GetPathComparisonKey(solutionPath);
        }

        return solutionPath.Trim().Replace('\\', '/');
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (_, session) in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
        _creationLock.Dispose();
    }
}

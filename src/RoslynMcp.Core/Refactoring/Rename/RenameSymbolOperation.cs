using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Renames any symbol with automatic reference updates across the solution.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and renames every eligible declaration whose simple name
/// equals <c>symbolName</c> to <c>newName</c>, skipping ineligible targets
/// rather than throwing.
/// </summary>
public sealed class RenameSymbolOperation : RefactoringOperationBase<RenameSymbolParams>
{
    private static readonly Regex IdentifierPattern = new(
        @"^@?[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Creates a new rename symbol operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public RenameSymbolOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RenameSymbolParams @params) => Validate(@params);

    /// <summary>
    /// Validates rename-symbol inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(RenameSymbolParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.NewName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newName is required.");

        if (!IdentifierPattern.IsMatch(@params.NewName))
            throw new RefactoringException(ErrorCodes.InvalidNewName, $"'{@params.NewName}' is not a valid C# identifier.");

        // Verbatim identifiers escape keywords; bare keywords remain reserved.
        if (!@params.NewName.StartsWith("@", StringComparison.Ordinal)
            && SyntaxFacts.GetKeywordKind(@params.NewName) != SyntaxKind.None)
        {
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"'{@params.NewName}' is a C# reserved keyword.");
        }

        if (@params.SymbolName == @params.NewName)
            throw new RefactoringException(ErrorCodes.SameLocation, "New name is the same as current name.");

        if (@params.AllFiles)
        {
            if (@params.Line.HasValue || @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with line or column.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
    }

    private static void ValidateSourceFilePath(string sourceFile)
    {
        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RenameSymbolParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        // Find the symbol
        var (symbol, document) = await FindSymbolAsync(@params, cancellationToken);

        // Validate rename is allowed
        ValidateRename(symbol, @params);

        // Find all references before rename
        var references = await ReferenceTracker.FindAllReferencesAsync(symbol, cancellationToken);

        // Compute rename options. Roslyn's Renamer always updates interface
        // implementations; renameImplementations: false is honored after the rename.
        var options = new SymbolRenameOptions(
            RenameOverloads: @params.RenameOverloads,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false // We handle file rename separately
        );

        IReadOnlyList<MemberIdentity> interfaceMembersToPreserve = [];
        MemberIdentity? selectedImplementation = null;
        if (!@params.RenameImplementations)
        {
            interfaceMembersToPreserve = CollectInterfaceMembers(
                symbol,
                @params.RenameOverloads,
                Context.Solution);
            if (interfaceMembersToPreserve.Count > 0
                && symbol.ContainingType?.TypeKind != TypeKind.Interface)
            {
                selectedImplementation = MemberIdentity.From(symbol, Context.Solution);
            }
        }

        // Perform the rename
        var newSolution = await Renamer.RenameSymbolAsync(
            Context.Solution,
            symbol,
            options,
            @params.NewName,
            cancellationToken);

        if (!@params.RenameImplementations && interfaceMembersToPreserve.Count > 0)
        {
            newSolution = await RestoreImplementationNamesAsync(
                newSolution,
                interfaceMembersToPreserve,
                selectedImplementation,
                GetRestoreName(symbol),
                @params.NewName,
                cancellationToken);
        }

        // Handle file rename for types
        string? renamedFile = null;
        if (@params.RenameFile && symbol is INamedTypeSymbol namedType && document.FilePath != null)
        {
            var fileName = Path.GetFileNameWithoutExtension(document.FilePath);
            if (fileName == symbol.Name)
            {
                var newFileName = @params.NewName + ".cs";
                var newFilePath = Path.Combine(Path.GetDirectoryName(document.FilePath)!, newFileName);

                // Rename the document in the solution
                var doc = newSolution.GetDocument(document.Id);
                if (doc != null)
                {
                    newSolution = newSolution.WithDocumentFilePath(document.Id, newFilePath);
                    renamedFile = newFilePath;
                }
            }
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, symbol, @params, references.TotalReferenceCount, renamedFile);
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        // Handle physical file rename
        string? fileRenameWarning = null;
        bool fileRenameSucceeded = false;
        if (renamedFile != null && document.FilePath != null)
        {
            try
            {
                if (File.Exists(document.FilePath) && !File.Exists(renamedFile))
                {
                    File.Move(document.FilePath, renamedFile);
                    fileRenameSucceeded = true;
                }
                else if (File.Exists(renamedFile))
                {
                    fileRenameWarning = $"File rename skipped: target file '{renamedFile}' already exists.";
                }
            }
            catch (IOException ex)
            {
                fileRenameWarning = $"File rename failed: {ex.Message}. Code references were updated but file was not renamed.";
                renamedFile = null; // Clear to indicate file was not actually renamed
            }
        }

        var changes = new FileChanges
        {
            FilesModified = commitResult.FilesModified,
            FilesCreated = fileRenameSucceeded ? commitResult.FilesCreated.Concat(new[] { renamedFile! }).ToList() : commitResult.FilesCreated,
            FilesDeleted = fileRenameSucceeded ? commitResult.FilesDeleted.Concat(new[] { document.FilePath! }).ToList() : commitResult.FilesDeleted
        };

        var result = RefactoringResult.Succeeded(
            operationId,
            changes,
            CreateSymbolInfo(symbol, @params.NewName, document.FilePath, fileRenameSucceeded ? renamedFile : null),
            references.TotalReferenceCount,
            0);

        // Include warning in result if file rename failed
        if (fileRenameWarning != null)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = result.OperationId,
                Preview = result.Preview,
                Changes = result.Changes,
                Symbol = result.Symbol,
                ReferencesUpdated = result.ReferencesUpdated,
                UsingDirectivesAdded = result.UsingDirectivesAdded,
                UsingDirectivesRemoved = result.UsingDirectivesRemoved,
                ExecutionTimeMs = result.ExecutionTimeMs,
                Error = RefactoringError.Create("PARTIAL_SUCCESS", fileRenameWarning),
                PendingChanges = result.PendingChanges
            };
        }

        return result;
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>RenameNamespaceOperation.ExecuteAllFilesAsync</c>)
    /// and renames every eligible declaration whose simple name equals
    /// <paramref name="params"/>.SymbolName to <paramref name="params"/>.NewName.
    /// Optional <c>sourceFile</c> limits via <see cref="DocumentSourceFileFilter"/>.
    /// Linked multi-project views of the same path are skipped rather than
    /// coalescing (same contract as <c>SafeDeleteOperation.ExecuteAllFilesAsync</c> /
    /// rename_namespace / remove_parameter allFiles). Symbols already at
    /// <c>newName</c>, name-conflict cases, constructors/destructors/operators,
    /// uneditable / source-generated docs, colliding <c>renameFile</c>
    /// destinations, and otherwise inapplicable declarations are skipped rather
    /// than failing the walk. Deduplicates by symbol identity (and overload group
    /// when <c>renameOverloads</c> expands the set) so partials / linked views
    /// are not double-applied. Deterministic <c>SpanStart</c> order within a file.
    /// The document-group walk repeats until a full pass makes no progress.
    /// When every file is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        RenameSymbolParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);
        var linkedPathCounts = AllFilesDocumentHelpers.BuildLinkedPathCounts(originalSolution);
        var renamedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var claimedFileDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedFileRenames = new List<(string SourcePath, string DestinationPath)>();
        var changedCountByDoc = new Dictionary<DocumentId, int>();

        bool madeProgress;
        do
        {
            madeProgress = false;

            foreach (var linkedDocuments in documentGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (linkedDocuments.Count > 1)
                    continue;

                var primary = linkedDocuments.FirstOrDefault(d =>
                    d is not SourceGeneratedDocument &&
                    DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace));
                if (primary == null)
                    continue;

                while (true)
                {
                    Context.UpdateSolution(currentSolution);

                    var currentDocument = currentSolution.GetDocument(primary.Id);
                    if (currentDocument == null ||
                        currentDocument is SourceGeneratedDocument ||
                        !DocumentEditableHelpers.IsDocumentEditable(currentDocument, Context.Workspace))
                    {
                        break;
                    }

                    var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                    var semanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
                    if (root == null || semanticModel == null)
                        break;

                    Solution? updated = null;
                    (string SourcePath, string DestinationPath)? fileRename = null;
                    foreach (var decl in CollectNamedDeclarations(root, semanticModel, @params.SymbolName, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            (updated, fileRename) = await TryRenameOneAsync(
                                currentDocument,
                                semanticModel,
                                decl,
                                @params,
                                renamedSymbols,
                                claimedFileDestinations,
                                linkedPathCounts,
                                cancellationToken);
                        }
                        catch (RefactoringException)
                        {
                            updated = null;
                            fileRename = null;
                        }

                        if (updated != null)
                            break;
                    }

                    if (updated == null)
                        break;

                    var beforeSolution = currentSolution;
                    currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                        beforeSolution,
                        updated,
                        Context.Workspace,
                        cancellationToken);
                    Context.UpdateSolution(currentSolution);

                    if (fileRename is { } rename)
                    {
                        plannedFileRenames.Add(rename);
                        claimedFileDestinations.Add(PathResolver.GetPathComparisonKey(rename.DestinationPath));
                    }

                    changedCountByDoc[primary.Id] =
                        changedCountByDoc.GetValueOrDefault(primary.Id) + 1;
                    madeProgress = true;
                }
            }
        } while (madeProgress);

        var documentsToCompare = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);
        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;
        var previewedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documentsToCompare)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalDocument = originalSolution.GetDocument(document.Id);
            var currentDocument = currentSolution.GetDocument(document.Id);
            if (originalDocument == null || currentDocument == null)
                continue;

            var beforeText = await originalDocument.GetTextAsync(cancellationToken);
            var afterText = await currentDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var pathKey = PathResolver.GetPathComparisonKey(originalDocument.FilePath!);
                if (!previewedPaths.Add(pathKey))
                    continue;

                var changedCount = changedCountByDoc.GetValueOrDefault(document.Id);
                if (changedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        changedCount = Math.Max(changedCount, changedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = changedCount > 0
                        ? BuildAllFilesDescription(changedCount, @params.SymbolName, @params.NewName)
                        : $"Rename '{@params.SymbolName}' to '{@params.NewName}'"
                });
                continue;
            }

            anyChanged = true;
        }

        if (@params.Preview)
        {
            foreach (var (sourcePath, destinationPath) in plannedFileRenames)
            {
                allPendingChanges.Add(new PendingChange
                {
                    File = destinationPath,
                    ChangeType = ChangeKind.Create,
                    Description = "Rename file to match type name"
                });
                allPendingChanges.Add(new PendingChange
                {
                    File = sourcePath,
                    ChangeType = ChangeKind.Delete,
                    Description = $"Rename '{Path.GetFileName(sourcePath)}' to '{Path.GetFileName(destinationPath)}'"
                });
            }

            Context.UpdateSolution(originalSolution);
            return RefactoringResult.PreviewResult(operationId, allPendingChanges);
        }

        Context.UpdateSolution(originalSolution);

        if (!anyChanged && plannedFileRenames.Count == 0)
        {
            return RefactoringResult.Succeeded(operationId,
                new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
                null, 0, 0);
        }

        var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
        var filesModified = commitResult.FilesModified.ToList();
        var filesCreated = commitResult.FilesCreated.ToList();
        var filesDeleted = commitResult.FilesDeleted.ToList();

        foreach (var (sourcePath, destinationPath) in plannedFileRenames)
        {
            try
            {
                if (!File.Exists(sourcePath))
                    continue;

                // CommitChangesAsync writes to the WithDocumentFilePath destination,
                // so the destination often already exists here. Move when absent;
                // otherwise delete the stale source that still holds the old type.
                if (!File.Exists(destinationPath))
                    File.Move(sourcePath, destinationPath);
                else
                    File.Delete(sourcePath);

                var destKey = PathResolver.GetPathComparisonKey(destinationPath);
                filesModified.RemoveAll(p =>
                    PathResolver.GetPathComparisonKey(p) == destKey);
                if (!filesCreated.Any(p => PathResolver.GetPathComparisonKey(p) == destKey))
                    filesCreated.Add(destinationPath);
                var sourceKey = PathResolver.GetPathComparisonKey(sourcePath);
                if (!filesDeleted.Any(p => PathResolver.GetPathComparisonKey(p) == sourceKey))
                    filesDeleted.Add(sourcePath);
            }
            catch (IOException)
            {
                // Skip failed physical renames; code references already updated.
            }
        }

        return RefactoringResult.Succeeded(operationId,
            new FileChanges
            {
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted
            },
            null, 0, 0);
    }

    /// <summary>
    /// Preview description for a file that renamed
    /// <paramref name="changedCount"/> symbols from
    /// <paramref name="symbolName"/> to <paramref name="newName"/>.
    /// </summary>
    internal static string BuildAllFilesDescription(int changedCount, string symbolName, string newName) =>
        changedCount == 1
            ? $"Rename '{symbolName}' to '{newName}'"
            : $"Rename {changedCount} symbols '{symbolName}' to '{newName}'";

    /// <summary>
    /// Declarations in <paramref name="root"/> whose declared symbol simple
    /// name equals <paramref name="symbolName"/>, in deterministic
    /// <c>SpanStart</c> then span-length order.
    /// </summary>
    internal static IReadOnlyList<SyntaxNode> CollectNamedDeclarations(
        SyntaxNode root,
        SemanticModel semanticModel,
        string symbolName,
        CancellationToken cancellationToken)
    {
        var results = new List<(SyntaxNode Node, int SpanStart, int Length)>();
        foreach (var node in root.DescendantNodes())
        {
            var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (symbol != null && symbol.Name == symbolName)
                results.Add((node, node.SpanStart, node.Span.Length));
        }

        return results
            .OrderBy(r => r.SpanStart)
            .ThenBy(r => r.Length)
            .Select(r => r.Node)
            .ToList();
    }

    private async Task<(Solution? Solution, (string SourcePath, string DestinationPath)? FileRename)> TryRenameOneAsync(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode declaration,
        RenameSymbolParams @params,
        HashSet<ISymbol> renamedSymbols,
        HashSet<string> claimedFileDestinations,
        IReadOnlyDictionary<string, int> linkedPathCounts,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
        if (symbol == null || symbol.Name != @params.SymbolName)
            return (null, null);

        if (renamedSymbols.Contains(symbol))
            return (null, null);

        if (string.Equals(symbol.Name, @params.NewName, StringComparison.Ordinal))
            return (null, null);

        try
        {
            ValidateRename(symbol, @params);
        }
        catch (RefactoringException)
        {
            return (null, null);
        }

        if (HasSimpleNameConflict(symbol, @params.NewName))
            return (null, null);

        var options = new SymbolRenameOptions(
            RenameOverloads: @params.RenameOverloads,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false);

        IReadOnlyList<MemberIdentity> interfaceMembersToPreserve = [];
        MemberIdentity? selectedImplementation = null;
        if (!@params.RenameImplementations)
        {
            interfaceMembersToPreserve = CollectInterfaceMembers(
                symbol,
                @params.RenameOverloads,
                Context.Solution);
            if (interfaceMembersToPreserve.Count > 0
                && symbol.ContainingType?.TypeKind != TypeKind.Interface)
            {
                selectedImplementation = MemberIdentity.From(symbol, Context.Solution);
            }
        }

        var beforeSolution = document.Project.Solution;
        Solution newSolution;
        try
        {
            newSolution = await Renamer.RenameSymbolAsync(
                beforeSolution,
                symbol,
                options,
                @params.NewName,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return (null, null);
        }

        if (!@params.RenameImplementations && interfaceMembersToPreserve.Count > 0)
        {
            newSolution = await RestoreImplementationNamesAsync(
                newSolution,
                interfaceMembersToPreserve,
                selectedImplementation,
                GetRestoreName(symbol),
                @params.NewName,
                cancellationToken);
        }

        if (ChangedDocumentsTouchLinkedMultiView(beforeSolution, newSolution, linkedPathCounts))
            return (null, null);

        ValidateAllFilesChangedDocumentsAreEditable(beforeSolution, newSolution);

        // Mark after a successful rewrite so renameOverloads expansion and
        // partials of this symbol are not selected again on later passes.
        MarkRenamed(renamedSymbols, symbol, @params.RenameOverloads);

        (string SourcePath, string DestinationPath)? fileRename = null;
        if (@params.RenameFile
            && symbol is INamedTypeSymbol
            && document.FilePath != null)
        {
            var fileName = Path.GetFileNameWithoutExtension(document.FilePath);
            if (fileName == symbol.Name)
            {
                var newFilePath = Path.Combine(
                    Path.GetDirectoryName(document.FilePath)!,
                    @params.NewName + ".cs");
                var destKey = PathResolver.GetPathComparisonKey(newFilePath);
                if (!claimedFileDestinations.Contains(destKey)
                    && !File.Exists(newFilePath)
                    && !string.Equals(
                        PathResolver.GetPathComparisonKey(document.FilePath),
                        destKey,
                        StringComparison.Ordinal))
                {
                    var doc = newSolution.GetDocument(document.Id);
                    if (doc != null)
                    {
                        newSolution = newSolution.WithDocumentFilePath(document.Id, newFilePath);
                        fileRename = (document.FilePath, newFilePath);
                    }
                }
            }
        }

        return (newSolution, fileRename);
    }

    private static void MarkRenamed(HashSet<ISymbol> renamedSymbols, ISymbol symbol, bool renameOverloads)
    {
        renamedSymbols.Add(symbol);
        if (!renameOverloads || symbol is not IMethodSymbol method || method.ContainingType == null)
            return;

        foreach (var overload in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
            renamedSymbols.Add(overload);
    }

    /// <summary>
    /// True when a sibling member / type / method-scoped name already uses
    /// <paramref name="newName"/> in the same container (cases single-site
    /// Renamer would leave conflicted). Locals, parameters, and method type
    /// parameters are checked against the enclosing method's lexical scope —
    /// <see cref="INamedTypeSymbol.GetMembers(string)"/> cannot see them.
    /// </summary>
    internal static bool HasSimpleNameConflict(ISymbol symbol, string newName)
    {
        if (IsMethodScopedSymbol(symbol))
            return HasMethodScopeNameConflict(symbol, newName);

        if (symbol.ContainingType != null)
        {
            foreach (var member in symbol.ContainingType.GetMembers(newName))
            {
                if (!SymbolEqualityComparer.Default.Equals(member, symbol))
                    return true;
            }

            return false;
        }

        if (symbol is INamedTypeSymbol namedType && namedType.ContainingNamespace != null)
        {
            foreach (var member in namedType.ContainingNamespace.GetMembers(newName))
            {
                if (member is INamedTypeSymbol existing
                    && !SymbolEqualityComparer.Default.Equals(existing, namedType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Locals, parameters, and method type parameters live in a method's
    /// lexical declaration space rather than on <see cref="INamedTypeSymbol"/>.
    /// </summary>
    internal static bool IsMethodScopedSymbol(ISymbol symbol) =>
        symbol.Kind is Microsoft.CodeAnalysis.SymbolKind.Local or Microsoft.CodeAnalysis.SymbolKind.Parameter
        || symbol is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method };

    /// <summary>
    /// Enclosing <see cref="IMethodSymbol"/> for a method-scoped symbol
    /// (walks through local functions / transparent enclosing symbols).
    /// </summary>
    internal static IMethodSymbol? GetEnclosingMethod(ISymbol symbol)
    {
        for (var current = symbol.ContainingSymbol; current != null; current = current.ContainingSymbol)
        {
            if (current is IMethodSymbol method)
                return method;
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="newName"/> collides with a parameter, method
    /// type parameter, local, or local function in the enclosing method.
    /// Conservative: any same-name local in the method body counts as a
    /// conflict (sibling-block renames may be skipped; skip-not-throw).
    /// </summary>
    internal static bool HasMethodScopeNameConflict(ISymbol symbol, string newName)
    {
        var method = GetEnclosingMethod(symbol);
        if (method == null)
            return false;

        foreach (var parameter in method.Parameters)
        {
            if (parameter.Name == newName
                && !SymbolEqualityComparer.Default.Equals(parameter, symbol))
            {
                return true;
            }
        }

        foreach (var typeParameter in method.TypeParameters)
        {
            if (typeParameter.Name == newName
                && !SymbolEqualityComparer.Default.Equals(typeParameter, symbol))
            {
                return true;
            }
        }

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            var body = GetMethodBodySyntax(syntax);
            if (body == null)
                continue;

            foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Identifier.ValueText != newName)
                    continue;
                if (IsDeclaredBySymbol(symbol, declarator.Span))
                    continue;
                return true;
            }

            foreach (var designation in body.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
            {
                if (designation.Identifier.ValueText != newName)
                    continue;
                if (IsDeclaredBySymbol(symbol, designation.Span))
                    continue;
                return true;
            }

            foreach (var localFunction in body.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                if (localFunction.Identifier.ValueText != newName)
                    continue;
                if (IsDeclaredBySymbol(symbol, localFunction.Identifier.Span))
                    continue;
                return true;
            }
        }

        return false;
    }

    private static SyntaxNode? GetMethodBodySyntax(SyntaxNode methodSyntax) =>
        methodSyntax switch
        {
            MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
            LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
            AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
            ConstructorDeclarationSyntax ctor => (SyntaxNode?)ctor.Body ?? ctor.ExpressionBody,
            DestructorDeclarationSyntax dtor => (SyntaxNode?)dtor.Body ?? dtor.ExpressionBody,
            OperatorDeclarationSyntax op => (SyntaxNode?)op.Body ?? op.ExpressionBody,
            ConversionOperatorDeclarationSyntax conv => (SyntaxNode?)conv.Body ?? conv.ExpressionBody,
            _ => methodSyntax
        };

    private static bool IsDeclaredBySymbol(ISymbol symbol, TextSpan span)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax();
            if (node.Span.Contains(span) || span.Contains(node.Span) || node.Span.OverlapsWith(span))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the rename rewrite touches any document whose path has
    /// multiple linked views (so Coalesce would overwrite siblings).
    /// </summary>
    internal static bool ChangedDocumentsTouchLinkedMultiView(
        Solution beforeSolution,
        Solution afterSolution,
        IReadOnlyDictionary<string, int> linkedPathCounts)
    {
        foreach (var projectChange in afterSolution.GetChanges(beforeSolution).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var document = beforeSolution.GetDocument(documentId)
                    ?? afterSolution.GetDocument(documentId);
                if (document != null && AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(document, linkedPathCounts))
                    return true;
            }
        }

        return false;
    }

    private void ValidateAllFilesChangedDocumentsAreEditable(Solution oldSolution, Solution newSolution)
    {
        foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var document = newSolution.GetDocument(documentId)
                    ?? oldSolution.GetDocument(documentId);
                if (document == null)
                    continue;

                DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);
            }
        }
    }

    private async Task<(ISymbol Symbol, Document Document)> FindSymbolAsync(
        RenameSymbolParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // If line/column provided, find symbol at position
        if (@params.Line.HasValue)
        {
            var position = SymbolResolver.GetPosition(root, @params.Line.Value, @params.Column ?? 1);
            var token = root.FindToken(position);

            // Walk up to find the symbol declaration or reference
            var node = token.Parent;
            while (node != null)
            {
                var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                if (symbol != null && symbol.Name == @params.SymbolName)
                {
                    return (symbol, document);
                }

                // Check for symbol info (reference)
                var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
                if (symbolInfo.Symbol != null && symbolInfo.Symbol.Name == @params.SymbolName)
                {
                    return (symbolInfo.Symbol, document);
                }

                node = node.Parent;
            }

            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{@params.SymbolName}' found at line {@params.Line}.");
        }

        // Otherwise, search by name
        var candidates = new List<ISymbol>();

        foreach (var node in root.DescendantNodes())
        {
            var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (symbol != null && symbol.Name == @params.SymbolName)
            {
                candidates.Add(symbol);
            }
        }

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{@params.SymbolName}' found in file.");
        }

        if (candidates.Count > 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple symbols named '{@params.SymbolName}' found. Provide line number to disambiguate.",
                new Dictionary<string, object>
                {
                    ["candidateCount"] = candidates.Count
                });
        }

        return (candidates[0], document);
    }

    private static void ValidateRename(ISymbol symbol, RenameSymbolParams @params)
    {
        // Cannot rename constructors directly
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.Constructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameConstructor,
                    "Cannot rename constructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.Destructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameDestructor,
                    "Cannot rename destructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.UserDefinedOperator ||
                method.MethodKind == MethodKind.Conversion)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameOperator,
                    "Cannot rename operators.");
            }
        }

        // Cannot rename symbols from external assemblies
        if (symbol.ContainingAssembly != null &&
            !symbol.Locations.Any(l => l.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.CannotRenameExternal,
                "Cannot rename symbols from external assemblies.");
        }
    }

    /// <summary>
    /// Creates symbol information for the result, with safe null handling for locations.
    /// </summary>
    private static Contracts.Models.SymbolInfo CreateSymbolInfo(
        ISymbol symbol,
        string newName,
        string? previousFile,
        string? newFile)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        FileLinePositionSpan? lineSpan = null;

        // Safely get line span only if location is valid and in source
        if (location != null && location.IsInSource)
        {
            try
            {
                var span = location.GetLineSpan();
                // Validate the span has meaningful data
                if (span.Path != null || span.StartLinePosition.Line >= 0)
                {
                    lineSpan = span;
                }
            }
            catch (InvalidOperationException)
            {
                // GetLineSpan can throw if location is invalid - treat as no location
            }
        }

        SymbolLocation? prevLocation = null;
        SymbolLocation? newLocation = null;

        if (previousFile != null && lineSpan.HasValue)
        {
            prevLocation = new SymbolLocation
            {
                File = previousFile,
                Line = lineSpan.Value.StartLinePosition.Line + 1,
                Column = lineSpan.Value.StartLinePosition.Character + 1
            };
        }

        if (newFile != null)
        {
            // Use line span if available, otherwise default to line 1, column 1
            newLocation = new SymbolLocation
            {
                File = newFile,
                Line = lineSpan?.StartLinePosition.Line + 1 ?? 1,
                Column = lineSpan?.StartLinePosition.Character + 1 ?? 1
            };
        }

        return new Contracts.Models.SymbolInfo
        {
            Name = newName,
            FullyQualifiedName = symbol.ToDisplayString().Replace(symbol.Name, newName),
            Kind = MapSymbolKind(symbol),
            PreviousLocation = prevLocation,
            NewLocation = newLocation
        };
    }

    private static Contracts.Enums.SymbolKind MapSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => Contracts.Enums.SymbolKind.Class,
                TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
                TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
                TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
                TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
                _ when namedType.IsRecord => Contracts.Enums.SymbolKind.Record,
                _ => Contracts.Enums.SymbolKind.Class
            },
            IMethodSymbol => Contracts.Enums.SymbolKind.Method,
            IPropertySymbol => Contracts.Enums.SymbolKind.Property,
            IFieldSymbol => Contracts.Enums.SymbolKind.Field,
            IEventSymbol => Contracts.Enums.SymbolKind.Event,
            ILocalSymbol => Contracts.Enums.SymbolKind.Local,
            IParameterSymbol => Contracts.Enums.SymbolKind.Parameter,
            INamespaceSymbol => Contracts.Enums.SymbolKind.Namespace,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ISymbol symbol,
        RenameSymbolParams @params,
        int referenceCount,
        string? renamedFile)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = $"Rename '{symbol.Name}' to '{@params.NewName}'"
            }
        };

        if (referenceCount > 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = "(multiple files)",
                ChangeType = ChangeKind.Modify,
                Description = $"Update {referenceCount} reference(s)"
            });
        }

        if (renamedFile != null)
        {
            pendingChanges.Add(new PendingChange
            {
                File = renamedFile,
                ChangeType = ChangeKind.Create,
                Description = "Rename file to match type name"
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    /// <summary>
    /// Collects interface members whose implementations should keep the original
    /// name when <c>renameImplementations</c> is false.
    /// </summary>
    private static List<MemberIdentity> CollectInterfaceMembers(
        ISymbol symbol,
        bool renameOverloads,
        Solution solution)
    {
        var result = new List<MemberIdentity>();

        if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
        {
            AddInterfaceMember(result, symbol, renameOverloads, solution);
            return result;
        }

        foreach (var implemented in GetImplementedInterfaceMembers(symbol))
            AddInterfaceMember(result, implemented, renameOverloads, solution);

        return result;
    }

    private static void AddInterfaceMember(
        List<MemberIdentity> result,
        ISymbol interfaceMember,
        bool renameOverloads,
        Solution solution)
    {
        result.Add(MemberIdentity.From(interfaceMember, solution));
        if (!renameOverloads || interfaceMember is not IMethodSymbol method)
            return;

        foreach (var overload in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
            result.Add(MemberIdentity.From(overload, solution));
    }

    private static IEnumerable<ISymbol> GetImplementedInterfaceMembers(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                foreach (var implemented in method.ExplicitInterfaceImplementations)
                    yield return implemented;
                break;
            case IPropertySymbol property:
                foreach (var implemented in property.ExplicitInterfaceImplementations)
                    yield return implemented;
                break;
            case IEventSymbol @event:
                foreach (var implemented in @event.ExplicitInterfaceImplementations)
                    yield return implemented;
                break;
        }

        var containing = symbol.ContainingType;
        if (containing == null)
            yield break;

        foreach (var iface in containing.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(symbol.Name))
            {
                var implementation = containing.FindImplementationForInterfaceMember(member);
                if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, symbol))
                    yield return member;
            }
        }
    }

    private async Task<Solution> RestoreImplementationNamesAsync(
        Solution solution,
        IReadOnlyList<MemberIdentity> interfaceMembers,
        MemberIdentity? selectedImplementation,
        string originalName,
        string newName,
        CancellationToken cancellationToken)
    {
        var spansByDocument = new Dictionary<DocumentId, HashSet<TextSpan>>();

        foreach (var identity in interfaceMembers)
        {
            var ifaceMember = await ResolveMemberAsync(solution, identity, newName, cancellationToken);
            if (ifaceMember == null)
                continue;

            var implementations = await SymbolFinder.FindImplementationsAsync(
                ifaceMember,
                solution,
                cancellationToken: cancellationToken);

            foreach (var impl in implementations)
            {
                if (impl is INamedTypeSymbol)
                    continue;

                if (selectedImplementation is { } selected && selected.SameMemberIgnoringName(impl))
                    continue;

                await AddImplementationNameSpansAsync(
                    solution,
                    impl,
                    spansByDocument,
                    cancellationToken);
            }
        }

        return await ApplyNameRestoresAsync(
            solution,
            spansByDocument,
            newName,
            originalName,
            cancellationToken);
    }

    private static async Task<ISymbol?> ResolveMemberAsync(
        Solution solution,
        MemberIdentity identity,
        string name,
        CancellationToken cancellationToken)
    {
        if (identity.DefiningProjectId is { } projectId)
        {
            var project = solution.GetProject(projectId);
            if (project != null)
            {
                var member = await TryResolveInProjectAsync(project, identity, name, cancellationToken);
                if (member != null)
                    return member;
            }
        }

        foreach (var project in solution.Projects)
        {
            if (identity.DefiningProjectId != null && project.Id == identity.DefiningProjectId)
                continue;

            var member = await TryResolveInProjectAsync(project, identity, name, cancellationToken);
            if (member != null)
                return member;
        }

        return null;
    }

    private static async Task<ISymbol?> TryResolveInProjectAsync(
        Project project,
        MemberIdentity identity,
        string name,
        CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
            return null;

        if (!string.IsNullOrEmpty(identity.AssemblyName)
            && !string.Equals(compilation.AssemblyName, identity.AssemblyName, StringComparison.Ordinal)
            && !string.Equals(compilation.Assembly.Name, identity.AssemblyName, StringComparison.Ordinal))
        {
            return null;
        }

        // Look up on this project's assembly only so a referenced type with the
        // same metadata name cannot steal the restore.
        var type = compilation.Assembly.GetTypeByMetadataName(identity.ContainingTypeMetadataName);
        if (type == null)
            return null;

        foreach (var member in type.GetMembers(name))
        {
            if (identity.Matches(member))
                return member;
        }

        return null;
    }

    private static async Task AddImplementationNameSpansAsync(
        Solution solution,
        ISymbol implementation,
        Dictionary<DocumentId, HashSet<TextSpan>> spansByDocument,
        CancellationToken cancellationToken)
    {
        foreach (var syntaxRef in implementation.DeclaringSyntaxReferences)
        {
            var node = await syntaxRef.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(syntaxRef.SyntaxTree);
            if (document == null)
                continue;

            var span = TryGetDeclarationNameSpan(node);
            if (span is { } nameSpan)
                AddSpan(spansByDocument, document.Id, nameSpan);
        }

        var references = await SymbolFinder.FindReferencesAsync(
            implementation,
            solution,
            cancellationToken);

        foreach (var referenced in references)
        {
            // Renamer cascades to the interface member; only revert the
            // implementing symbol and its own references.
            if (referenced.Definition.ContainingType?.TypeKind == TypeKind.Interface)
                continue;

            foreach (var location in referenced.Locations)
            {
                if (location.IsImplicit || !location.Location.IsInSource)
                    continue;

                AddSpan(spansByDocument, location.Document.Id, location.Location.SourceSpan);
            }
        }
    }

    private static SyntaxToken? TryGetDeclarationIdentifier(SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax method => method.Identifier,
            PropertyDeclarationSyntax property => property.Identifier,
            EventDeclarationSyntax @event => @event.Identifier,
            VariableDeclaratorSyntax variable => variable.Identifier,
            EventFieldDeclarationSyntax eventField =>
                eventField.Declaration.Variables.FirstOrDefault()?.Identifier,
            _ => null
        };
    }

    private static TextSpan? TryGetDeclarationNameSpan(SyntaxNode node) =>
        TryGetDeclarationIdentifier(node)?.Span;

    /// <summary>
    /// Recovers the identifier text to write back when implementations are
    /// preserved. <see cref="ISymbol.Name"/> drops the <c>@</c> on verbatim
    /// keywords (<c>@class</c> becomes <c>class</c>), which would emit invalid C#.
    /// </summary>
    private static string GetRestoreName(ISymbol symbol)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var identifier = TryGetDeclarationIdentifier(syntaxRef.GetSyntax());
            if (identifier is { } token
                && token.ValueText == symbol.Name
                && !string.IsNullOrEmpty(token.Text))
            {
                return token.Text;
            }
        }

        return EscapeIfReservedKeyword(symbol.Name);
    }

    private static string EscapeIfReservedKeyword(string name)
    {
        if (name.StartsWith('@'))
            return name;

        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;
    }

    private static async Task<Solution> ApplyNameRestoresAsync(
        Solution solution,
        Dictionary<DocumentId, HashSet<TextSpan>> spansByDocument,
        string currentName,
        string originalName,
        CancellationToken cancellationToken)
    {
        foreach (var (documentId, spans) in spansByDocument)
        {
            var document = solution.GetDocument(documentId);
            if (document == null)
                continue;

            var text = await document.GetTextAsync(cancellationToken);
            var changes = spans
                .Where(span => span.End <= text.Length
                    && string.Equals(text.ToString(span), currentName, StringComparison.Ordinal))
                .OrderBy(span => span.Start)
                .Select(span => new TextChange(span, originalName))
                .ToList();

            if (changes.Count == 0)
                continue;

            solution = document.WithText(text.WithChanges(changes)).Project.Solution;
        }

        return solution;
    }

    private static void AddSpan(
        Dictionary<DocumentId, HashSet<TextSpan>> spansByDocument,
        DocumentId documentId,
        TextSpan span)
    {
        if (!spansByDocument.TryGetValue(documentId, out var spans))
        {
            spans = [];
            spansByDocument[documentId] = spans;
        }

        spans.Add(span);
    }

    private static string GetFullMetadataName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var current = type; current != null; current = current.ContainingType)
            parts.Add(current.MetadataName);

        parts.Reverse();
        var typeName = string.Join("+", parts);
        return type.ContainingNamespace.IsGlobalNamespace
            ? typeName
            : type.ContainingNamespace.ToDisplayString() + "." + typeName;
    }

    private static string[] GetParameterTypeKeys(ISymbol symbol)
    {
        var parameters = symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => default
        };

        if (parameters.IsDefaultOrEmpty)
            return [];

        return parameters
            .Select(p => $"{p.RefKind}:{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}")
            .ToArray();
    }

    private readonly record struct MemberIdentity(
        string ContainingTypeMetadataName,
        string AssemblyName,
        ProjectId? DefiningProjectId,
        Microsoft.CodeAnalysis.SymbolKind Kind,
        int Arity,
        string[] ParameterTypeKeys)
    {
        public bool Matches(ISymbol symbol)
        {
            if (symbol.Kind != Kind)
                return false;

            if (symbol is IMethodSymbol method && method.Arity != Arity)
                return false;

            return GetParameterTypeKeys(symbol).SequenceEqual(ParameterTypeKeys, StringComparer.Ordinal);
        }

        public bool SameMemberIgnoringName(ISymbol symbol)
        {
            return symbol.ContainingType != null
                && ContainingTypeMetadataName == GetFullMetadataName(symbol.ContainingType)
                && (string.IsNullOrEmpty(AssemblyName)
                    || string.Equals(AssemblyName, symbol.ContainingAssembly?.Name, StringComparison.Ordinal))
                && Matches(symbol);
        }

        public static MemberIdentity From(ISymbol symbol, Solution solution)
        {
            var type = symbol.ContainingType
                ?? throw new ArgumentException("Symbol must be a type member.", nameof(symbol));

            return new MemberIdentity(
                GetFullMetadataName(type),
                symbol.ContainingAssembly?.Name ?? string.Empty,
                GetDefiningProjectId(symbol, solution),
                symbol.Kind,
                symbol is IMethodSymbol method ? method.Arity : 0,
                GetParameterTypeKeys(symbol));
        }

        private static ProjectId? GetDefiningProjectId(ISymbol symbol, Solution solution)
        {
            foreach (var location in symbol.Locations)
            {
                if (!location.IsInSource || location.SourceTree == null)
                    continue;

                var document = solution.GetDocument(location.SourceTree);
                if (document != null)
                    return document.Project.Id;
            }

            return null;
        }
    }
}

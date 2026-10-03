using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared allFiles document enumerate / linked-path group / linked-text
/// coalesce walk used by IntroduceField / ExtractConstant /
/// IntroduceParameter / InlineMethod / ChangeSignature / ExtractInterface
/// (and Enumerate / GroupBy peers), plus EnumerateCsharpDocumentsIncludingAdded
/// for PullMembersUp / PushMembersDown / ExtractInterface / ExtractBaseClass
/// documentsToCompare (original + newly-added .cs), plus EnumerateCsharpDocuments for
/// ConvertToBlockBody / AddBraces / RemoveBraces / InvertIf /
/// ConvertExpressionBody / ConvertToPatternMatching / ConvertProperty /
/// ConvertForeachLinq / SimplifyName / ConvertToInterpolatedString /
/// ConvertToAsync / AddMissingUsings / RemoveUnusedUsings / SortUsings /
/// FormatDocument / EncapsulateField / AddNullChecks / UseBaseType /
/// RenameFileToMatchType / MoveTypeToNamespace / MoveTypeToFile
/// (and Generate*/Implement*/InlineConstant/MakeStatic peers),
/// plus BuildLinkedPathCounts /
/// DocumentPathHasLinkedMultiView for AddParameter / RemoveParameter /
/// ReorderParameters / RenameNamespace / PushMembersDown, plus
/// ChangedDocumentsTouchLinkedMultiView for RenameSymbol / RenameNamespace, plus
/// FilterAllFilesDocumentsBySourceFile for ExtractBaseClass /
/// ExtractInterface / ExtractMethod / ExtractConstant / ExtractVariable /
/// PushMembersDown / PullMembersUp / ConvertToBlockBody / FormatDocument / AddMissingUsings / RemoveUnusedUsings / SortUsings / SimplifyName / AddBraces / RemoveBraces / InlineConstant /
/// InlineVariable / InlineMethod / MakeStatic / MakeNonStatic / IntroduceParameter /
/// RenameSymbol / RenameNamespace / AddParameter / RemoveParameter / ReorderParameters /
/// ChangeSignature / ChangeReturnType / ImplementInterface / ImplementAbstract /
/// GenerateOverrides / GenerateMethodStub / GenerateProperty /
/// GenerateConstructor / GenerateEqualsHashCode / GenerateToString /
/// EncapsulateField / AddNullChecks / UseBaseType / RenameFileToMatchType /
/// MoveTypeToFile / MoveTypeToNamespace (default path) and
/// IntroduceField / SafeDelete (optional rejectMissingPathCasingMismatch arm).
/// Same bodies as the identical copies on those operations.
/// Named AllFilesDocumentHelpers (not DocumentSourceFileFilter) because this
/// cluster is the walk + linked-sibling coalesce + exact/ignore-case
/// optional-<c>sourceFile</c> limiter used together by those operations;
/// the lower-level ignore-case path-key match stays on
/// <see cref="DocumentSourceFileFilter"/>.
/// </summary>
internal static class AllFilesDocumentHelpers
{
    /// <summary>
    /// Every solution document whose FilePath ends with <c>.cs</c>
    /// (ordinal-ignore-case), ordered by FilePath. Same body as the
    /// IntroduceField / ExtractConstant / IntroduceParameter / InlineMethod /
    /// ChangeSignature / InlineVariable copies (ExecuteAllFiles start and
    /// documentsToCompare). Call sites at ExecuteAllFiles start also include
    /// ConvertToBlockBody / AddBraces / RemoveBraces / InvertIf /
    /// ConvertExpressionBody / ConvertToPatternMatching / ConvertProperty /
    /// ConvertForeachLinq / SimplifyName / ConvertToInterpolatedString /
    /// ConvertToAsync / AddMissingUsings / RemoveUnusedUsings / SortUsings /
    /// FormatDocument / EncapsulateField / AddNullChecks / UseBaseType /
    /// RenameFileToMatchType / MoveTypeToNamespace / MoveTypeToFile (those
    /// Convert / Organize / Format / Encapsulate / Generate / Hierarchy /
    /// Rename / MoveType callers previously omitted OrderBy except
    /// MoveTypeToNamespace, which already ordered; migrating onto this helper
    /// adds the deterministic FilePath ordinal walk where it was missing).
    /// </summary>
    internal static List<Document> EnumerateCsharpDocuments(Solution solution)
    {
        return solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every C# document from <paramref name="originalSolution"/> plus any
    /// newly-added <c>.cs</c> documents present only on
    /// <paramref name="currentSolution"/> (FilePath non-null, EndsWith
    /// <c>.cs</c> ordinal-ignore-case, <c>originalSolution.GetDocument(Id)</c>
    /// null). Deduped by DocumentId (first wins) and ordered by FilePath
    /// ordinal. Same body as the identical <c>documentsToCompare</c> constructions
    /// on PullMembersUp / PushMembersDown / ExtractInterface / ExtractBaseClass.
    /// </summary>
    internal static List<Document> EnumerateCsharpDocumentsIncludingAdded(
        Solution originalSolution,
        Solution currentSolution)
    {
        return EnumerateCsharpDocuments(originalSolution)
            .Concat(
                currentSolution.Projects
                    .SelectMany(p => p.Documents)
                    .Where(d => d.FilePath != null &&
                                d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                                originalSolution.GetDocument(d.Id) == null))
            .GroupBy(d => d.Id)
            .Select(g => g.First())
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Groups documents by <see cref="PathResolver.GetPathComparisonKey"/> so
    /// linked multi-project views of the same physical path are walked once.
    /// Within each group and across groups, order by FilePath / Project.Name /
    /// DocumentId. Same body as the IntroduceField / ExtractConstant /
    /// IntroduceParameter / InlineMethod / ChangeSignature / InlineVariable /
    /// ConvertToBlockBody copies.
    /// </summary>
    internal static List<List<Document>> GroupByLinkedPath(IEnumerable<Document> documents)
    {
        return documents
            .GroupBy(d => PathResolver.GetPathComparisonKey(d.FilePath!), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                .ToList())
            .OrderBy(group => group[0].FilePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// After a rewrite from <paramref name="beforeSolution"/> to
    /// <paramref name="updatedSolution"/>, copy rewritten text onto editable
    /// linked siblings that share a path-comparison key. Prefer a DocumentId
    /// that actually changed so an unchanged sorted-first sibling cannot
    /// overwrite the rewrite. Same body as the five IntroduceField /
    /// ExtractConstant / IntroduceParameter / InlineMethod / ChangeSignature
    /// copies.
    /// </summary>
    internal static async Task<Solution> CoalesceLinkedDocumentTextAsync(
        Solution beforeSolution,
        Solution updatedSolution,
        Microsoft.CodeAnalysis.Workspace workspace,
        CancellationToken cancellationToken)
    {
        var currentSolution = updatedSolution;
        var changedDocIds = new HashSet<DocumentId>();
        var changedPathKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectChanges in updatedSolution.GetChanges(beforeSolution).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                changedDocIds.Add(docId);
                var changedDoc = updatedSolution.GetDocument(docId);
                if (changedDoc?.FilePath != null)
                    changedPathKeys.Add(PathResolver.GetPathComparisonKey(changedDoc.FilePath));
            }
        }

        if (changedPathKeys.Count > 0)
        {
            var allCurrent = currentSolution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null)
                .ToList();

            foreach (var pathKey in changedPathKeys)
            {
                var siblings = allCurrent
                    .Where(d => PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                    .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                    .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                    .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                    .ToList();
                if (siblings.Count <= 1)
                    continue;

                var sourceDoc = siblings.FirstOrDefault(d =>
                        changedDocIds.Contains(d.Id) &&
                        d is not SourceGeneratedDocument &&
                        DocumentEditableHelpers.IsDocumentEditable(d, workspace))
                    ?? siblings.FirstOrDefault(d =>
                        d is not SourceGeneratedDocument &&
                        DocumentEditableHelpers.IsDocumentEditable(d, workspace));
                if (sourceDoc == null)
                    continue;

                var live = currentSolution.GetDocument(sourceDoc.Id);
                if (live == null)
                    continue;
                var sharedText = await live.GetTextAsync(cancellationToken);

                foreach (var sibling in siblings)
                {
                    if (sibling.Id == sourceDoc.Id)
                        continue;
                    var siblingLive = currentSolution.GetDocument(sibling.Id);
                    if (siblingLive == null || siblingLive is SourceGeneratedDocument)
                        continue;
                    if (!DocumentEditableHelpers.IsDocumentEditable(siblingLive, workspace))
                        continue;
                    currentSolution = currentSolution.WithDocumentText(sibling.Id, sharedText);
                }
            }
        }

        return currentSolution;
    }

    /// <summary>
    /// Path → linked-view count across the entire solution (not a filtered
    /// <c>sourceFile</c> subset). Same body as the identical copies on
    /// AddParameter / RemoveParameter / ReorderParameters /
    /// RenameNamespace / PushMembersDown.
    /// </summary>
    internal static Dictionary<string, int> BuildLinkedPathCounts(Solution solution)
    {
        var groups = GroupByLinkedPath(EnumerateCsharpDocuments(solution));
        return groups
            .Where(g => g.Count > 0 && g[0].FilePath != null)
            .GroupBy(g => PathResolver.GetPathComparisonKey(g[0].FilePath!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="document"/> shares a physical path with
    /// multiple linked workspace views. Same body as the identical copies on
    /// AddParameter / RemoveParameter / ReorderParameters / RenameNamespace.
    /// </summary>
    internal static bool DocumentPathHasLinkedMultiView(
        Document document,
        IReadOnlyDictionary<string, int> linkedPathCounts)
    {
        if (document.FilePath == null)
            return false;

        var pathKey = PathResolver.GetPathComparisonKey(document.FilePath);
        return linkedPathCounts.TryGetValue(pathKey, out var count) && count > 1;
    }

    /// <summary>
    /// True when the rewrite from <paramref name="beforeSolution"/> to
    /// <paramref name="afterSolution"/> touches any document whose path has
    /// multiple linked views (so coalesce would overwrite siblings). Same
    /// body as the identical copies on RenameSymbol / RenameNamespace.
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
                if (document != null && DocumentPathHasLinkedMultiView(document, linkedPathCounts))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Limits an allFiles document list to an optional caller-supplied
    /// <paramref name="sourceFile"/>. Prefer exact
    /// <see cref="PathResolver.NormalizePath"/> matches (and every linked
    /// sibling that shares a path-comparison key with those exact hits);
    /// otherwise fall back to
    /// <see cref="DocumentSourceFileFilter.FilterDocumentsBySourceFile"/>
    /// and require a single distinct comparison-key path. Throws
    /// <see cref="RefactoringException"/> with
    /// <see cref="ErrorCodes.SourceFileNotFound"/> /
    /// <see cref="ErrorCodes.SourceNotInWorkspace"/> when nothing matches or
    /// multiple ignore-case paths collide. Default path
    /// (<paramref name="rejectMissingPathCasingMismatch"/> false) matches
    /// ExtractBaseClass / ExtractInterface / ExtractMethod / ExtractConstant /
    /// ExtractVariable / PushMembersDown / PullMembersUp / ConvertToBlockBody /
    /// FormatDocument / AddMissingUsings / RemoveUnusedUsings / SortUsings / AddBraces / RemoveBraces / InlineConstant / InlineVariable / InlineMethod / MakeStatic /
    /// MakeNonStatic / IntroduceParameter / RenameSymbol / RenameNamespace /
    /// AddParameter / RemoveParameter / ReorderParameters / ChangeSignature /
    /// ChangeReturnType / ImplementInterface / ImplementAbstract /
    /// GenerateOverrides / GenerateMethodStub / GenerateProperty /
    /// GenerateConstructor / GenerateEqualsHashCode / GenerateToString /
    /// EncapsulateField / AddNullChecks / UseBaseType / RenameFileToMatchType /
    /// MoveTypeToFile / MoveTypeToNamespace.
    /// When
    /// <paramref name="rejectMissingPathCasingMismatch"/>
    /// is true (IntroduceField / SafeDelete), also reject an ignore-case hit
    /// whose comparison key differs from the caller path when that path does
    /// not exist on disk.
    /// </summary>
    internal static List<Document> FilterAllFilesDocumentsBySourceFile(
        List<Document> documents,
        string sourceFile,
        bool rejectMissingPathCasingMismatch = false)
    {
        var normalizedSourceFile = PathResolver.NormalizePath(sourceFile);
        var sourceFileKey = rejectMissingPathCasingMismatch
            ? PathResolver.GetPathComparisonKey(sourceFile)
            : null;
        var exactMatches = documents
            .Where(d => string.Equals(PathResolver.NormalizePath(d.FilePath!), normalizedSourceFile, StringComparison.Ordinal))
            .ToList();
        if (exactMatches.Count > 0)
        {
            var exactKeys = exactMatches
                .Select(d => PathResolver.GetPathComparisonKey(d.FilePath!))
                .ToHashSet(StringComparer.Ordinal);
            return documents
                .Where(d => exactKeys.Contains(PathResolver.GetPathComparisonKey(d.FilePath!)))
                .ToList();
        }

        var matchedDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(documents, normalizedSourceFile);
        var distinctPaths = matchedDocuments
            .Select(d => PathResolver.GetPathComparisonKey(d.FilePath!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return distinctPaths.Count switch
        {
            0 when !File.Exists(sourceFile) => throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"Source file not found: {sourceFile}"),
            0 => throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"File not found in workspace: {sourceFile}"),
            > 1 => throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"Multiple workspace files match path ignoring case: {sourceFile}. Use the exact file path casing."),
            _ when rejectMissingPathCasingMismatch
                && !string.Equals(distinctPaths[0], sourceFileKey, StringComparison.Ordinal)
                && !File.Exists(sourceFile) =>
                throw new RefactoringException(
                    ErrorCodes.SourceFileNotFound,
                    $"Source file not found: {sourceFile}"),
            _ => matchedDocuments
        };
    }
}

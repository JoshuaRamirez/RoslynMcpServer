using Microsoft.CodeAnalysis;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared allFiles document enumerate / linked-path group / linked-text
/// coalesce walk used by IntroduceField / ExtractConstant /
/// IntroduceParameter / InlineMethod / ChangeSignature (and Enumerate /
/// GroupBy peers). Same bodies as the identical copies on those operations.
/// Named AllFilesDocumentHelpers (not DocumentSourceFileFilter) because this
/// cluster is the walk + linked-sibling coalesce used together by those
/// operations; sourceFile filtering stays on DocumentSourceFileFilter.
/// </summary>
internal static class AllFilesDocumentHelpers
{
    /// <summary>
    /// Every solution document whose FilePath ends with <c>.cs</c>
    /// (ordinal-ignore-case), ordered by FilePath. Same body as the
    /// IntroduceField / ExtractConstant / IntroduceParameter / InlineMethod /
    /// ChangeSignature / InlineVariable copies (ExecuteAllFiles start and
    /// documentsToCompare).
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
}

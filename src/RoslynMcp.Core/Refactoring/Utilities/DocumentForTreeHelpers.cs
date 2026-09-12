using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared SyntaxTree → Document lookup used by Generate / Hierarchy / Extract
/// operations that need a declaring document for a type (GetDocument(tree), then
/// FilePath fallback). Excludes GenerateMethodStub's nullable overload, which
/// returns null instead of throwing.
/// </summary>
internal static class DocumentForTreeHelpers
{
    /// <summary>
    /// Resolves the <see cref="Document"/> for <paramref name="tree"/> via
    /// <see cref="Solution.GetDocument(SyntaxTree)"/>, then
    /// <see cref="Solution.GetDocumentIdsWithFilePath"/> when the tree has a
    /// FilePath. Throws <see cref="ErrorCodes.DocumentNotEditable"/> when no
    /// document is found. Same body as the 10 Generate / Hierarchy / Extract
    /// copies (excludes GenerateMethodStub's nullable overload).
    /// </summary>
    internal static Document GetDocumentForTree(Solution solution, SyntaxTree tree, string typeName)
    {
        var document = solution.GetDocument(tree);
        if (document != null)
            return document;

        if (!string.IsNullOrEmpty(tree.FilePath))
        {
            foreach (var id in solution.GetDocumentIdsWithFilePath(tree.FilePath))
            {
                document = solution.GetDocument(id);
                if (document != null)
                    return document;
            }
        }

        throw new RefactoringException(
            ErrorCodes.DocumentNotEditable,
            $"Could not locate a declaring document for type '{typeName}'.");
    }
}

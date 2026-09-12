using Microsoft.CodeAnalysis;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared source-file document filter used by Generate / Extract / Inline
/// AllFiles paths that narrow candidate documents to a caller-supplied
/// <c>sourceFile</c> (PathResolver normalize + OrdinalIgnoreCase match).
/// </summary>
internal static class DocumentSourceFileFilter
{
    /// <summary>
    /// Keeps documents whose normalized FilePath equals
    /// the normalized <paramref name="sourceFile"/> (OrdinalIgnoreCase). When
    /// <see cref="PathResolver.NormalizePath"/> throws ArgumentException,
    /// NotSupportedException, or PathTooLongException for the requested path,
    /// the raw <paramref name="sourceFile"/> is used instead. Same body as the
    /// 9 MakeStatic / MakeNonStatic / GenerateOverrides / GenerateEqualsHashCode /
    /// GenerateConstructor / GenerateToString / ImplementInterface /
    /// ImplementAbstract / InlineConstant copies.
    /// </summary>
    internal static List<Document> FilterDocumentsBySourceFile(List<Document> documents, string sourceFile)
    {
        string wanted;
        try
        {
            wanted = PathResolver.NormalizePath(sourceFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            wanted = sourceFile;
        }

        return documents
            .Where(d => string.Equals(
                PathResolver.NormalizePath(d.FilePath!),
                wanted,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

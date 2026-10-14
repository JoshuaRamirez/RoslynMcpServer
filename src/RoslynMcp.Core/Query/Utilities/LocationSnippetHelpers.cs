using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Shared line-snippet body for <c>find_references</c> and <c>find_callers</c>: the trimmed text of
/// the start line at a source <see cref="Location"/>, or <c>null</c> when the location is not in
/// source, has no syntax tree, or the start line index is out of range.
/// </summary>
internal static class LocationSnippetHelpers
{
    /// <summary>
    /// Returns the trimmed text of the line that contains <paramref name="location"/>'s start
    /// position, or <c>null</c> when the location is not in source, has no <see cref="Location.SourceTree"/>,
    /// or the start line index is outside the source text.
    /// </summary>
    public static async Task<string?> GetLineSnippetAsync(Location location, CancellationToken cancellationToken)
    {
        if (!location.IsInSource) return null;

        var tree = location.SourceTree;
        if (tree == null) return null;

        var text = await tree.GetTextAsync(cancellationToken);
        var lineSpan = location.GetLineSpan();
        var lineIndex = lineSpan.StartLinePosition.Line;

        if (lineIndex < 0 || lineIndex >= text.Lines.Count) return null;

        return text.Lines[lineIndex].ToString().Trim();
    }
}

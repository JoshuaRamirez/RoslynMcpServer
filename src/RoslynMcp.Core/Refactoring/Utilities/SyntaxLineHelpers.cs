using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared syntax line-position helpers used by convert operations when
/// selecting a target by 1-based start line.
/// </summary>
internal static class SyntaxLineHelpers
{
    /// <summary>
    /// True when <paramref name="node"/>'s start line (1-based) equals
    /// <paramref name="line"/>. Same body as the two private copies on
    /// convert_to_interpolated_string / convert_to_pattern_matching.
    /// </summary>
    internal static bool StartsOnLine(SyntaxNode node, int line) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line;
}

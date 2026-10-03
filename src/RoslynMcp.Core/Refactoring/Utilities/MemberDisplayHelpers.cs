using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared member display / request-match helpers used by ImplementAbstract /
/// ImplementInterface for indexer parameter labels, pending-change
/// member-kind wording, and member-name request matching. Same bodies as
/// the prior private copies.
/// </summary>
internal static class MemberDisplayHelpers
{
    /// <summary>
    /// Formats an indexer parameter as <c>ref/out/in/ref readonly type name</c>
    /// (or bare <c>type name</c>) for member-request matching display.
    /// </summary>
    internal static string FormatIndexerParameterDisplay(IParameterSymbol parameter)
    {
        var type = parameter.Type.ToDisplayString();
        return parameter.RefKind switch
        {
            RefKind.Ref => $"ref {type} {parameter.Name}",
            RefKind.Out => $"out {type} {parameter.Name}",
            RefKind.In => $"in {type} {parameter.Name}",
            RefKind.RefReadOnlyParameter => $"ref readonly {type} {parameter.Name}",
            _ => $"{type} {parameter.Name}"
        };
    }

    /// <summary>
    /// Human-readable kind label for preview / pending-change descriptions:
    /// method, indexer, property, event, or member.
    /// </summary>
    internal static string DescribeMemberKind(ISymbol member) => member switch
    {
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => "member"
    };

    /// <summary>
    /// True when <paramref name="member"/> is named in <paramref name="requested"/>
    /// (ordinary <see cref="ISymbol.Name"/>), or when it is an indexer whose
    /// <see cref="ISymbol.MetadataName"/> or <c>this[…]</c> display forms
    /// (withNames / typesOnly / typesOnlySpaced via
    /// <see cref="FormatIndexerParameterDisplay"/>) appear in the set.
    /// Same body as the prior ImplementInterface / ImplementAbstract copies.
    /// </summary>
    internal static bool MatchesRequestedMember(ISymbol member, HashSet<string> requested)
    {
        if (requested.Contains(member.Name))
            return true;

        if (member is not IPropertySymbol { IsIndexer: true } indexer)
            return false;

        var withNames = $"this[{string.Join(", ", indexer.Parameters.Select(FormatIndexerParameterDisplay))}]";
        var typesOnly = $"this[{string.Join(",", indexer.Parameters.Select(p => p.Type.ToDisplayString()))}]";
        var typesOnlySpaced = $"this[{string.Join(", ", indexer.Parameters.Select(p => p.Type.ToDisplayString()))}]";
        return requested.Contains(indexer.MetadataName)
            || requested.Contains(withNames)
            || requested.Contains(typesOnly)
            || requested.Contains(typesOnlySpaced);
    }
}

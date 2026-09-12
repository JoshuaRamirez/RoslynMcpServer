using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared member display strings used by ImplementAbstract /
/// ImplementInterface for indexer parameter labels and pending-change
/// member-kind wording. Same bodies as the two private copies.
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
}

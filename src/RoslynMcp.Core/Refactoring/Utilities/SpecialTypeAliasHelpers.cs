using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared special-type keyword aliases and requested-type matching used by
/// generate_property / generate_method_stub. Same bodies as the prior private
/// <c>TryGetSpecialTypeAlias</c> copies (MethodStub's complete alias map) and
/// the identical <c>PropertyTypeMatchesField</c> /
/// <c>ParameterTypeMatches</c> HashSet matchers.
/// </summary>
internal static class SpecialTypeAliasHelpers
{
    /// <summary>
    /// Maps <paramref name="specialType"/> to its C# keyword alias when one
    /// exists (<c>int</c>, <c>string</c>, <c>nint</c>, <c>void</c>, …).
    /// Same body as generate_method_stub's former private copy (superset of
    /// generate_property's former map).
    /// </summary>
    internal static bool TryGetSpecialTypeAlias(SpecialType specialType, out string alias)
    {
        alias = specialType switch
        {
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_Char => "char",
            SpecialType.System_IntPtr => "nint",
            SpecialType.System_UIntPtr => "nuint",
            SpecialType.System_String => "string",
            SpecialType.System_Object => "object",
            SpecialType.System_Void => "void",
            _ => ""
        };
        return alias.Length > 0;
    }

    /// <summary>
    /// True when <paramref name="requestedType"/> matches
    /// <paramref name="type"/> via display string, FQN without
    /// <c>global::</c>, simple <see cref="ISymbol.Name"/>, or the special-type
    /// keyword alias from <see cref="TryGetSpecialTypeAlias"/>. Same body as
    /// generate_property's <c>PropertyTypeMatchesField</c> and
    /// generate_method_stub's <c>ParameterTypeMatches</c>.
    /// </summary>
    /// <param name="requestedType">The caller-supplied type text to match.</param>
    /// <param name="type">The symbol's type to compare against.</param>
    /// <param name="includeNativeAndVoidAliases">
    /// When false, the <c>nint</c>/<c>nuint</c>/<c>void</c> aliases are not
    /// added as candidates. generate_property passes false to keep its original
    /// 15-entry alias map (no widening for IntPtr/UIntPtr/Void fields).
    /// </param>
    internal static bool MatchesRequestedType(
        string requestedType,
        ITypeSymbol type,
        bool includeNativeAndVoidAliases = true)
    {
        requestedType = requestedType.Trim();
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            type.ToDisplayString(),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "", StringComparison.Ordinal),
            type.Name
        };

        if (TryGetSpecialTypeAlias(type.SpecialType, out var alias)
            && (includeNativeAndVoidAliases || !IsNativeOrVoidSpecialType(type.SpecialType)))
            candidates.Add(alias);

        return candidates.Contains(requestedType);
    }

    /// <summary>
    /// True for the special types whose aliases (<c>nint</c>, <c>nuint</c>,
    /// <c>void</c>) were only in generate_method_stub's former map.
    /// </summary>
    internal static bool IsNativeOrVoidSpecialType(SpecialType specialType) =>
        specialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_Void;
}

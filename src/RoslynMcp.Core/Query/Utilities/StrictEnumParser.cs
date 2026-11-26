namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Parses a string parameter as one of an enum's defined member names only.
/// </summary>
/// <remarks>
/// Unlike <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>, numeric strings ("0", "3",
/// "99", "-1") and comma-separated combinations ("Ancestors, Descendants", "Error, Info") are
/// rejected instead of being turned into undefined or OR-combined values (#3130). Matching is
/// case-insensitive on the trimmed value, the same contract as <see cref="SymbolKindFilterParser"/>.
/// </remarks>
internal static class StrictEnumParser
{
    /// <summary>
    /// Tries to match <paramref name="value"/> (trimmed, case-insensitive) to a defined member name of
    /// <typeparamref name="TEnum"/>.
    /// </summary>
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        if (value == null)
            return false;

        var trimmed = value.Trim();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        return false;
    }
}

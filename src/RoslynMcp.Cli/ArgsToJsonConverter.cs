using System.Reflection;
using System.Text.Json;

namespace RoslynMcp.Cli;

/// <summary>
/// Converts CLI option dictionaries (kebab-case keys) to JSON strings (camelCase keys)
/// suitable for deserializing into params DTOs.
/// </summary>
public static class ArgsToJsonConverter
{
    /// <summary>
    /// Convert a dictionary of kebab-case CLI options to a camelCase JSON string.
    /// </summary>
    /// <remarks>
    /// Rules:
    /// - --kebab-case → camelCase key
    /// - Numeric string values → JSON numbers
    /// - "true"/"false" (case-insensitive) → JSON booleans
    /// - Everything else → JSON strings
    /// </remarks>
    public static string Convert(Dictionary<string, string> options) => Convert(options, paramsType: null);

    /// <summary>
    /// Convert a dictionary of kebab-case CLI options to a camelCase JSON string, using
    /// <paramref name="paramsType"/> to bind string-list options (e.g. <c>--diagnostic-ids</c>,
    /// <c>--exclude-diagnostic-ids</c>, <c>--members</c>) as JSON arrays.
    /// </summary>
    /// <remarks>
    /// Same rules as <see cref="Convert(Dictionary{string, string})"/>, plus: when
    /// <paramref name="paramsType"/> has a public property matching the camelCase key whose type is a
    /// string collection (<c>IReadOnlyList&lt;string&gt;</c>, <c>List&lt;string&gt;</c>, <c>string[]</c>, …),
    /// the value is emitted as a JSON array of strings. A value that is a JSON array literal
    /// (e.g. <c>["CS1591","CS8019"]</c>) is used as-is; otherwise it is split on commas and each
    /// entry trimmed (e.g. <c>CS1591, CS8019</c>). A blank value becomes an empty array. When the matching
    /// property is a <see cref="string"/>, the value is always emitted as a JSON string (no boolean or
    /// numeric inference), so values such as <c>true</c> or <c>123</c> still bind to string parameters.
    /// </remarks>
    public static string Convert(Dictionary<string, string> options, Type? paramsType)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        foreach (var (kebabKey, value) in options)
        {
            var camelKey = KebabToCamel(kebabKey);

            if (paramsType is not null && IsStringListProperty(paramsType, camelKey))
            {
                WriteStringArray(writer, camelKey, value);
            }
            else if (paramsType is not null && IsStringProperty(paramsType, camelKey))
            {
                // A string-typed parameter keeps its raw value, so e.g. --name-filter 123 or
                // --query true is not inferred as a JSON number/boolean that cannot bind to string.
                writer.WriteString(camelKey, value);
            }
            else if (bool.TryParse(value, out var boolVal))
            {
                writer.WriteBoolean(camelKey, boolVal);
            }
            else if (long.TryParse(value, out var longVal))
            {
                writer.WriteNumber(camelKey, longVal);
            }
            else
            {
                writer.WriteString(camelKey, value);
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsStringProperty(Type paramsType, string camelKey) =>
        FindProperty(paramsType, camelKey)?.PropertyType == typeof(string);

    private static PropertyInfo? FindProperty(Type paramsType, string camelKey) =>
        paramsType.GetProperty(
            camelKey,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    private static bool IsStringListProperty(Type paramsType, string camelKey)
    {
        var prop = FindProperty(paramsType, camelKey);
        if (prop is null)
            return false;

        var type = prop.PropertyType;
        if (type == typeof(string[]))
            return true;

        if (!type.IsGenericType || type.GetGenericArguments()[0] != typeof(string))
            return false;

        var genDef = type.GetGenericTypeDefinition();
        return genDef == typeof(IReadOnlyList<>) ||
               genDef == typeof(IReadOnlyCollection<>) ||
               genDef == typeof(IList<>) ||
               genDef == typeof(ICollection<>) ||
               genDef == typeof(IEnumerable<>) ||
               genDef == typeof(List<>);
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string key, string value)
    {
        var trimmed = value.Trim();

        if (trimmed.StartsWith('['))
        {
            string?[]? items = null;
            try
            {
                items = JsonSerializer.Deserialize<string?[]>(trimmed);
            }
            catch (JsonException)
            {
                // Not a JSON array of strings; fall back to comma-splitting below.
            }

            if (items is not null)
            {
                writer.WriteStartArray(key);
                foreach (var item in items)
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
                return;
            }
        }

        writer.WriteStartArray(key);
        if (trimmed.Length > 0)
        {
            foreach (var item in trimmed.Split(','))
                writer.WriteStringValue(item.Trim());
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Convert a kebab-case string to camelCase.
    /// </summary>
    /// <example>"source-file" → "sourceFile", "line" → "line"</example>
    public static string KebabToCamel(string kebab)
    {
        if (string.IsNullOrEmpty(kebab))
            return kebab;

        var parts = kebab.Split('-');
        if (parts.Length == 1)
            return parts[0].ToLowerInvariant();

        // First segment is lowercase, subsequent segments are title-cased
        var result = parts[0].ToLowerInvariant();
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                result += char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
            }
        }

        return result;
    }
}

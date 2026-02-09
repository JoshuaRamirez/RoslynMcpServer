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
    public static string Convert(Dictionary<string, string> options)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        foreach (var (kebabKey, value) in options)
        {
            var camelKey = KebabToCamel(kebabKey);

            if (bool.TryParse(value, out var boolVal))
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

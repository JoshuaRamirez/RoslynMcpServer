using System.Reflection;

namespace RoslynMcp.Cli;

/// <summary>
/// Resolves relative CLI paths against the current working directory (#3061). The MCP server keeps
/// its absolute-path contract; only the command line accepts <c>Fx.csproj</c> or
/// <c>--source-file Foo.cs</c> the way its own <c>--help</c> examples use them.
/// </summary>
public static class CliPathResolver
{
    /// <summary>
    /// <paramref name="path"/> made absolute against the current directory; rooted or blank values
    /// are returned unchanged.
    /// </summary>
    public static string? ResolvePath(string? path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

    /// <summary>
    /// Resolves, in place, every option that binds to a string property of <paramref name="paramsType"/>
    /// whose name ends in <c>File</c> or <c>Path</c> (<c>--source-file</c>, <c>--target-file</c>,
    /// <c>--project-path</c>, …). Other options are left alone.
    /// </summary>
    public static void ResolvePathOptions(Dictionary<string, string> options, Type? paramsType)
    {
        if (paramsType is null)
            return;

        foreach (var key in options.Keys.ToList())
        {
            var property = paramsType.GetProperty(
                ArgsToJsonConverter.KebabToCamel(key),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property?.PropertyType != typeof(string))
                continue;
            if (!property.Name.EndsWith("File", StringComparison.Ordinal) &&
                !property.Name.EndsWith("Path", StringComparison.Ordinal))
            {
                continue;
            }

            options[key] = ResolvePath(options[key])!;
        }
    }
}

using System.Reflection;
using System.Text;

namespace RoslynMcp.Cli;

/// <summary>
/// Generates help text from the tool registry and params DTO reflection.
/// </summary>
public static class HelpGenerator
{
    /// <summary>
    /// Generate global help listing all available tools.
    /// </summary>
    public static string GenerateGlobalHelp(ToolRegistry registry)
    {
        var tools = registry.GetAllTools();
        var sb = new StringBuilder();

        sb.AppendLine("roslyn-cli — Standalone CLI for Roslyn-powered C# tools");
        sb.AppendLine();
        sb.AppendLine("USAGE:");
        sb.AppendLine("  roslyn-cli <solution-path> <tool-name> [--option value ...]");
        sb.AppendLine("  roslyn-cli <tool-name> --help");
        sb.AppendLine("  roslyn-cli --help");
        sb.AppendLine();
        sb.AppendLine("GLOBAL OPTIONS:");
        sb.AppendLine("  --format <json|text>  Output format (default: json)");
        sb.AppendLine("  --verbose             Show detailed output");
        sb.AppendLine("  --help, -h            Show this help");
        sb.AppendLine();

        var grouped = tools.GroupBy(t => t.Category).OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            sb.AppendLine($"{group.Key.ToUpperInvariant()} ({group.Count()}):");
            foreach (var tool in group)
            {
                sb.AppendLine($"  {tool.Name,-36} {tool.Description}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Total: {tools.Count} tools");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("  roslyn-cli MySolution.sln rename-symbol --source-file Foo.cs --symbol-name Bar --new-name Baz");
        sb.AppendLine("  roslyn-cli MySolution.sln get-diagnostics --severity-filter Error --format text");
        sb.AppendLine("  roslyn-cli MySolution.sln diagnose");

        return sb.ToString();
    }

    /// <summary>
    /// Generate help for a specific tool, reflecting on its params DTO.
    /// </summary>
    public static string GenerateToolHelp(ToolEntry tool)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{tool.Name} — {tool.Description}");
        sb.AppendLine();
        sb.AppendLine("USAGE:");
        if (tool.RequiresWorkspace)
            sb.AppendLine($"  roslyn-cli <solution-path> {tool.Name} [--option value ...]");
        else
            sb.AppendLine($"  roslyn-cli <solution-path> {tool.Name} [--option value ...]  (solution is optional)");
        sb.AppendLine();

        var props = tool.ParamsType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (props.Length == 0)
        {
            sb.AppendLine("  (no parameters)");
            return sb.ToString();
        }

        var required = new List<PropertyInfo>();
        var optional = new List<PropertyInfo>();

        foreach (var prop in props)
        {
            if (IsRequired(prop))
                required.Add(prop);
            else
                optional.Add(prop);
        }

        if (required.Count > 0)
        {
            sb.AppendLine("REQUIRED:");
            foreach (var prop in required)
                AppendParam(sb, prop);
            sb.AppendLine();
        }

        if (optional.Count > 0)
        {
            sb.AppendLine("OPTIONAL:");
            foreach (var prop in optional)
                AppendParam(sb, prop);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendParam(StringBuilder sb, PropertyInfo prop)
    {
        var kebabName = PascalToKebab(prop.Name);
        var typeName = GetFriendlyTypeName(prop.PropertyType);
        var desc = GetPropertyDescription(prop);

        sb.Append($"  --{kebabName,-30} {typeName}");
        if (!string.IsNullOrEmpty(desc))
            sb.Append($"  {desc}");
        sb.AppendLine();
    }

    private static bool IsRequired(PropertyInfo prop)
    {
        // Check for System.ComponentModel.DataAnnotations.RequiredAttribute
        var attrs = prop.GetCustomAttributes(true);
        if (attrs.Any(a => a.GetType().Name == "RequiredAttribute"))
            return true;

        // C# 11 'required' keyword: the compiler emits RequiredMemberAttribute on the
        // declaring type and marks each required property in metadata. We detect it by
        // checking CustomAttributeData (which captures the compiler-emitted attribute
        // that GetCustomAttributes() may not surface as an instantiated object).
        var declaringType = prop.DeclaringType;
        if (declaringType is not null &&
            declaringType.CustomAttributes.Any(a =>
                a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
        {
            // Type uses required members. Check if this specific property is required
            // via its CustomAttributeData or by detecting init-only + non-nullable.
            if (prop.CustomAttributes.Any(a =>
                    a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
                return true;

            // Fallback: init-only setter + non-nullable type implies required
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter is not null)
            {
                var returnParam = setter.ReturnParameter;
                var isInitOnly = returnParam.GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

                if (isInitOnly)
                {
                    if (prop.PropertyType.IsValueType)
                    {
                        // Non-nullable value type (T, not T?) implies required
                        if (Nullable.GetUnderlyingType(prop.PropertyType) is null)
                            return true;
                    }
                    else
                    {
                        // Non-nullable reference type implies required
                        var nullCtx = new NullabilityInfoContext().Create(prop);
                        if (nullCtx.WriteState == NullabilityState.NotNull)
                            return true;
                    }
                }
            }
        }

        return false;
    }

    private static string GetPropertyDescription(PropertyInfo prop)
    {
        // Try to get description from XML doc (not available at runtime without XML file parsing).
        // Fall back to property name humanization.
        return "";
    }

    private static string GetFriendlyTypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return GetFriendlyTypeName(underlying) + "?";

        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(Guid)) return "guid";

        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            if (genDef == typeof(List<>) || genDef == typeof(IReadOnlyList<>) || genDef == typeof(IList<>))
            {
                var elemType = GetFriendlyTypeName(type.GetGenericArguments()[0]);
                return $"{elemType}[]";
            }
        }

        if (type.IsEnum)
            return string.Join("|", Enum.GetNames(type));

        return type.Name;
    }

    /// <summary>
    /// Convert PascalCase to kebab-case, handling consecutive capitals (acronyms) correctly.
    /// E.g. "XMLPath" → "xml-path", "SourceFile" → "source-file", "IP" → "ip".
    /// </summary>
    public static string PascalToKebab(string pascal)
    {
        if (string.IsNullOrEmpty(pascal))
            return pascal;

        var sb = new StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    // Insert dash if previous char was lowercase (e.g. "lP" in "SymbolPath")
                    // or if this is the start of a new word after an acronym (e.g. "LP" in "XMLPath"
                    // where the next char 'a' is lowercase).
                    bool prevIsLower = char.IsLower(pascal[i - 1]);
                    bool nextIsLower = i + 1 < pascal.Length && char.IsLower(pascal[i + 1]);
                    if (prevIsLower || nextIsLower)
                        sb.Append('-');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

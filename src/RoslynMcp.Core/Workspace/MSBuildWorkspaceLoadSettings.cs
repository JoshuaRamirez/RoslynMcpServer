using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Workspace;

internal static class MSBuildWorkspaceLoadSettings
{
    private const string _nuGetRestoreMsbuildArgsEnvironmentVariableName = "NUGET_RESTORE_MSBUILD_ARGS";

    private static readonly string[] _passThroughPropertyNames =
    [
        "NuGetAudit",
        "TreatWarningsAsErrors",
        "WarningsNotAsErrors"
    ];

    private static readonly Regex _nuGetAuditDiagnosticPattern =
        new(
            @"\bNU190[1-4]\b|Package '[^']+'.* has a known [^,]+ vulnerability, https://github\.com/advisories/",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex _commandLinePropertySeparatorPattern =
        new(@"(?<=[^%])[;,](?=[A-Za-z_][A-Za-z0-9_.-]*=)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] _propertySwitchPrefixes =
    [
        "-property:",
        "-p:",
        "/property:",
        "/p:"
    ];

    internal static Dictionary<string, string> BuildGlobalProperties(
        Func<string, string?>? getEnvironmentVariable = null,
        IEnumerable<string>? commandLineArgs = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        commandLineArgs ??= [];

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        MergeKnownEnvironmentProperties(properties, getEnvironmentVariable);
        MergeNuGetRestoreMsbuildArgsProperties(properties, getEnvironmentVariable);
        MergeCommandLineProperties(properties, commandLineArgs);

        return properties;
    }

    internal static IReadOnlyList<WorkspaceDiagnostic> GetFatalDiagnostics(IEnumerable<WorkspaceDiagnostic> diagnostics) =>
        diagnostics
            .Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure && !IsIgnorableFailure(diagnostic))
            .ToList();

    internal static bool IsIgnorableFailure(WorkspaceDiagnostic diagnostic) =>
        diagnostic.Kind == WorkspaceDiagnosticKind.Failure &&
        _nuGetAuditDiagnosticPattern.IsMatch(diagnostic.Message);

    private static void MergeKnownEnvironmentProperties(
        IDictionary<string, string> properties,
        Func<string, string?> getEnvironmentVariable)
    {
        foreach (var propertyName in _passThroughPropertyNames)
        {
            var value = getEnvironmentVariable(propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                properties[propertyName] = value.Trim();
            }
        }
    }

    private static void MergeNuGetRestoreMsbuildArgsProperties(
        Dictionary<string, string> properties,
        Func<string, string?> getEnvironmentVariable)
    {
        var restoreMsbuildArgs = getEnvironmentVariable(_nuGetRestoreMsbuildArgsEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(restoreMsbuildArgs))
        {
            return;
        }

        MergeCommandLineProperties(properties, SplitCommandLineArguments(restoreMsbuildArgs));
    }

    private static void MergeCommandLineProperties(
        Dictionary<string, string> properties,
        IEnumerable<string> commandLineArgs)
    {
        foreach (var argument in commandLineArgs)
        {
            if (!TryGetPropertySwitchPayload(argument, out var payload))
            {
                continue;
            }

            foreach (var (name, value) in ParseCommandLineProperties(payload))
            {
                properties[name] = value;
            }
        }
    }

    private static Dictionary<string, string> ParseCommandLineProperties(string payload)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _commandLinePropertySeparatorPattern.Split(payload))
        {
            var equalsIndex = entry.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == entry.Length - 1)
            {
                continue;
            }

            var name = entry[..equalsIndex].Trim();
            var value = entry[(equalsIndex + 1)..].Trim();
            if (name.Length > 0)
            {
                properties[name] = value;
            }
        }

        return properties;
    }

    private static IEnumerable<string> SplitCommandLineArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            yield break;
        }

        var startIndex = 0;
        var inQuotes = false;

        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (index > startIndex)
                {
                    yield return commandLine[startIndex..index];
                }

                startIndex = index + 1;
            }
        }

        if (startIndex < commandLine.Length)
        {
            yield return commandLine[startIndex..];
        }
    }

    private static bool TryGetPropertySwitchPayload(string argument, out string payload)
    {
        foreach (var prefix in _propertySwitchPrefixes)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                payload = argument[prefix.Length..];
                return payload.Length > 0;
            }
        }

        payload = string.Empty;
        return false;
    }
}

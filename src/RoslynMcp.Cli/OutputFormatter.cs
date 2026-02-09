using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynMcp.Contracts.Models;

namespace RoslynMcp.Cli;

/// <summary>
/// Formats tool execution results for stdout output.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Format a result object as indented JSON.
    /// </summary>
    public static string FormatJson(object result) =>
        JsonSerializer.Serialize(result, result.GetType(), IndentedJson);

    /// <summary>
    /// Format a result object as human-readable text.
    /// </summary>
    public static string FormatText(object result)
    {
        if (result is RefactoringResult refactoring)
            return FormatRefactoringText(refactoring);

        // For query results, use reflection to access the generic QueryResult<T>
        var type = result.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(QueryResult<>))
            return FormatQueryResultText(result, type);

        // DiagnoseResult or other types
        if (result is DiagnoseResult diagnose)
            return FormatDiagnoseText(diagnose);

        // Fallback to JSON
        return FormatJson(result);
    }

    private static string FormatRefactoringText(RefactoringResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Success ? "OK" : "FAILED");

        if (!r.Success && r.Error is not null)
        {
            sb.AppendLine($"  Error: [{r.Error.Code}] {r.Error.Message}");
            return sb.ToString();
        }

        if (r.Preview)
        {
            sb.AppendLine("  Mode: Preview (no changes applied)");
            if (r.PendingChanges is { Count: > 0 })
                sb.AppendLine($"  Pending changes: {r.PendingChanges.Count}");
        }
        else
        {
            if (r.Changes is not null)
            {
                sb.AppendLine($"  Files modified: {r.Changes.FilesModified.Count}");
                sb.AppendLine($"  Files added: {r.Changes.FilesCreated.Count}");
                sb.AppendLine($"  Files deleted: {r.Changes.FilesDeleted.Count}");
            }
            sb.AppendLine($"  References updated: {r.ReferencesUpdated}");
        }

        sb.AppendLine($"  Duration: {r.ExecutionTimeMs}ms");
        return sb.ToString();
    }

    private static string FormatQueryResultText(object result, Type type)
    {
        var sb = new StringBuilder();

        var successProp = type.GetProperty("Success")!;
        var success = (bool)successProp.GetValue(result)!;
        sb.AppendLine(success ? "OK" : "FAILED");

        if (!success)
        {
            var errorProp = type.GetProperty("Error")!;
            var error = errorProp.GetValue(result) as Contracts.Errors.RefactoringError;
            if (error is not null)
                sb.AppendLine($"  Error: [{error.Code}] {error.Message}");
            return sb.ToString();
        }

        var timeProp = type.GetProperty("ExecutionTimeMs")!;
        var timeMs = (long)timeProp.GetValue(result)!;

        var dataProp = type.GetProperty("Data")!;
        var data = dataProp.GetValue(result);
        if (data is not null)
        {
            // Serialize the data portion as indented JSON for readability
            var dataJson = JsonSerializer.Serialize(data, data.GetType(), IndentedJson);
            sb.AppendLine(dataJson);
        }

        sb.AppendLine($"  Duration: {timeMs}ms");
        return sb.ToString();
    }

    private static string FormatDiagnoseText(DiagnoseResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Healthy ? "HEALTHY" : "UNHEALTHY");

        if (r.Components is not null)
        {
            sb.AppendLine($"  Roslyn: {(r.Components.RoslynAvailable ? "available" : "unavailable")} {r.Components.RoslynVersion}");
            sb.AppendLine($"  MSBuild: {(r.Components.MsBuildFound ? "found" : "not found")} {r.Components.MsBuildVersion}");
            sb.AppendLine($"  .NET SDK: {(r.Components.DotnetSdkAvailable ? "available" : "unavailable")} {r.Components.DotnetSdkVersion}");
        }

        if (r.Workspace is not null)
        {
            sb.AppendLine($"  Workspace: {r.Workspace.State}");
            if (r.Workspace.SolutionLoaded)
            {
                sb.AppendLine($"  Solution: {r.Workspace.SolutionPath}");
                sb.AppendLine($"  Projects: {r.Workspace.ProjectCount}");
                sb.AppendLine($"  Documents: {r.Workspace.DocumentCount}");
            }
        }

        if (r.Errors is { Count: > 0 })
        {
            foreach (var err in r.Errors)
                sb.AppendLine($"  Error: [{err.Code}] {err.Message}");
        }

        return sb.ToString();
    }
}

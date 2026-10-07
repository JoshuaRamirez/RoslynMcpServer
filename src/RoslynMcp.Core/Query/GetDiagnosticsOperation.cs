using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Retrieves compiler diagnostics for the solution or a specific file.
/// Delegates to Roslyn's Compilation.GetDiagnostics().
/// </summary>
public sealed class GetDiagnosticsOperation : QueryOperationBase<GetDiagnosticsParams, GetDiagnosticsResult>
{
    /// <inheritdoc />
    public GetDiagnosticsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GetDiagnosticsParams @params)
    {
        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

            if (!File.Exists(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
        }

        if (!string.IsNullOrWhiteSpace(@params.SeverityFilter) &&
            !Enum.TryParse<DiagnosticSeverityFilter>(@params.SeverityFilter, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<DiagnosticSeverityFilter>());
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"Invalid severityFilter. Valid values: {valid}");
        }

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (@params.DiagnosticIds != null && @params.DiagnosticIds.Any(string.IsNullOrWhiteSpace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "diagnosticIds entries must be non-empty.");

        if (@params.ExcludeDiagnosticIds != null && @params.ExcludeDiagnosticIds.Any(string.IsNullOrWhiteSpace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "excludeDiagnosticIds entries must be non-empty.");

        ProjectPathFilter.Validate(@params.ProjectPath);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetDiagnosticsResult>> ExecuteCoreAsync(
        Guid operationId,
        GetDiagnosticsParams @params,
        CancellationToken cancellationToken)
    {
        var severityFilter = ParseSeverityFilter(@params.SeverityFilter);
        var idFilter = BuildIdFilter(@params.DiagnosticIds);
        var excludeIdFilter = BuildIdFilter(@params.ExcludeDiagnosticIds);
        var diagnostics = new List<DiagnosticInfo>();

        foreach (var project in ProjectPathFilter.SelectProjects(Context.Solution, @params.ProjectPath))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            foreach (var diag in compilation.GetDiagnostics(cancellationToken))
            {
                if (!PassesSeverityFilter(diag.Severity, severityFilter))
                    continue;

                if (idFilter != null && !idFilter.Contains(diag.Id))
                    continue;

                // Exclusion wins when an ID is in both diagnosticIds and excludeDiagnosticIds.
                if (excludeIdFilter != null && excludeIdFilter.Contains(diag.Id))
                    continue;

                // Filter by file if specified
                if (!string.IsNullOrWhiteSpace(@params.SourceFile) && diag.Location.IsInSource)
                {
                    var diagPath = diag.Location.GetLineSpan().Path;
                    if (!string.Equals(diagPath, @params.SourceFile, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (!string.IsNullOrWhiteSpace(@params.SourceFile) && !diag.Location.IsInSource)
                {
                    continue;
                }

                string? file = null;
                int line = 0;
                int column = 0;

                if (diag.Location.IsInSource)
                {
                    var lineSpan = diag.Location.GetLineSpan();
                    file = lineSpan.Path;
                    line = lineSpan.StartLinePosition.Line + 1;
                    column = lineSpan.StartLinePosition.Character + 1;
                }

                var info = new DiagnosticInfo
                {
                    Id = diag.Id,
                    Message = diag.GetMessage(),
                    Severity = diag.Severity.ToString(),
                    Category = diag.Descriptor.Category,
                    File = file,
                    Line = line,
                    Column = column
                };

                diagnostics.Add(info);
            }
        }

        var totalCount = diagnostics.Count;
        IReadOnlyList<DiagnosticInfo> returned = diagnostics;
        if (@params.MaxResults.HasValue && diagnostics.Count > @params.MaxResults.Value)
        {
            returned = diagnostics.Take(@params.MaxResults.Value).ToList();
        }

        var result = new GetDiagnosticsResult
        {
            Diagnostics = returned,
            TotalCount = totalCount,
            Truncated = totalCount > returned.Count
        };

        return QueryResult<GetDiagnosticsResult>.Succeeded(operationId, result);
    }

    private static HashSet<string>? BuildIdFilter(IReadOnlyList<string>? diagnosticIds)
    {
        if (diagnosticIds == null || diagnosticIds.Count == 0)
            return null;

        return new HashSet<string>(diagnosticIds.Select(id => id.Trim()), StringComparer.OrdinalIgnoreCase);
    }

    private static DiagnosticSeverityFilter ParseSeverityFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return DiagnosticSeverityFilter.Warning;

        return Enum.Parse<DiagnosticSeverityFilter>(filter, ignoreCase: true);
    }

    private static bool PassesSeverityFilter(DiagnosticSeverity severity, DiagnosticSeverityFilter filter)
    {
        return filter switch
        {
            DiagnosticSeverityFilter.Error => severity == DiagnosticSeverity.Error,
            DiagnosticSeverityFilter.Warning => severity >= DiagnosticSeverity.Warning,
            DiagnosticSeverityFilter.Info => severity >= DiagnosticSeverity.Info,
            DiagnosticSeverityFilter.Hidden => true,
            DiagnosticSeverityFilter.All => true,
            _ => severity >= DiagnosticSeverity.Warning
        };
    }
}

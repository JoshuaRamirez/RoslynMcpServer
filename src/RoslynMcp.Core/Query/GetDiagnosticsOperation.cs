using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
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
            if (!PathResolver.IsAbsolutePath(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

            if (!File.Exists(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
        }

        if (!string.IsNullOrWhiteSpace(@params.SeverityFilter) &&
            !Enum.TryParse<DiagnosticSeverityFilter>(@params.SeverityFilter, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<DiagnosticSeverityFilter>());
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"Invalid severityFilter. Valid values: {valid}");
        }
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetDiagnosticsResult>> ExecuteCoreAsync(
        Guid operationId,
        GetDiagnosticsParams @params,
        CancellationToken cancellationToken)
    {
        var severityFilter = ParseSeverityFilter(@params.SeverityFilter);
        var diagnostics = new List<DiagnosticInfo>();

        foreach (var project in Context.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            foreach (var diag in compilation.GetDiagnostics(cancellationToken))
            {
                if (!PassesSeverityFilter(diag.Severity, severityFilter))
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

        var result = new GetDiagnosticsResult
        {
            Diagnostics = diagnostics,
            TotalCount = diagnostics.Count
        };

        return QueryResult<GetDiagnosticsResult>.Succeeded(operationId, result);
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Retrieves compiler diagnostics for the solution or a specific file.
/// Delegates to Roslyn's Compilation.GetDiagnostics(), optionally narrowed to diagnostic sites in one
/// namespace (<see cref="GetDiagnosticsParams.NamespaceFilter"/>) and/or of one declared accessibility
/// (<see cref="GetDiagnosticsParams.AccessibilityFilter"/>), and/or of one symbol kind
/// (<see cref="GetDiagnosticsParams.KindFilter"/>), and/or whose name contains a substring
/// (<see cref="GetDiagnosticsParams.NameFilter"/>), and/or to static or instance sites
/// (<see cref="GetDiagnosticsParams.StaticFilter"/>), and/or to messages containing a substring
/// (<see cref="GetDiagnosticsParams.MessageFilter"/>).
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

        AccessibilityFilterParser.Parse(@params.AccessibilityFilter);

        // Same parsing and InvalidSymbolKind error as search_symbols / find_references /
        // find_callers kindFilter.
        SymbolKindFilterParser.Parse(@params.KindFilter);
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

        // Optional messageFilter (same normalization / matching as the shared nameFilter): ordinal,
        // case-insensitive substring on the formatted message returned in DiagnosticInfo.Message. Null
        // when omitted / blank (no message filtering).
        var messageFilter = NameFilterMatcher.Normalize(@params.MessageFilter);
        var diagnostics = new List<DiagnosticInfo>();

        // Optional accessibilityFilter (same values / parsing as search_symbols / find_references
        // accessibilityFilter): keep only diagnostics whose site symbol has a DeclaredAccessibility
        // equal to the filter exactly. The site prefers the nearest declared member at the diagnostic
        // span (declaration-attached diagnostics such as CS0169 on a field identifier) and falls back
        // to the enclosing symbol (same notion namespaceFilter uses), then looks through lambdas /
        // local functions / locals / params to the declaring member (see DeclaringSiteSymbol). Null
        // when omitted / blank (no accessibility filtering). Invalid values already rejected in
        // ValidateParams.
        var accessibilityFilter = AccessibilityFilterParser.Parse(@params.AccessibilityFilter);

        // Optional kindFilter (same values / parsing as search_symbols / find_references /
        // find_callers kindFilter): keep only diagnostics whose site symbol (the same
        // DeclaringSiteSymbol accessibilityFilter judges) maps to that kind via SymbolKindMapper
        // (methods / constructors / accessors → Method, including the implicit getter whose body is an
        // expression-bodied property / indexer's `=> expr`; fields → Field or Constant; property
        // definitions → Property). Namespace-level sites never match a named kind filter. Null when
        // omitted / blank. Invalid values already rejected in ValidateParams.
        var kindFilter = SymbolKindFilterParser.Parse(@params.KindFilter);

        // Optional nameFilter (same normalization / matching as find_references / find_callers
        // nameFilter): keep only diagnostics whose site symbol (the same DeclaringSiteSymbol
        // accessibilityFilter / kindFilter judge) has a simple ISymbol.Name containing the value
        // (ordinal, case-insensitive; accessors read get_X / set_X, constructors .ctor).
        // Namespace-level sites never match. Null when omitted / blank (no name filtering).
        var nameFilter = NameFilterMatcher.Normalize(@params.NameFilter);

        // Optional staticFilter (same ISymbol.IsStatic test as get_symbol_info / get_document_outline /
        // search_symbols / find_callers / find_references / find_implementations staticFilter): keep only
        // diagnostics whose site symbol (the same DeclaringSiteSymbol accessibilityFilter / kindFilter /
        // nameFilter judge) is static (true) or instance (false). Namespace-level sites never match either
        // value. Null when omitted.
        var staticFilter = @params.StaticFilter;

        // accessibilityFilter / kindFilter / nameFilter / staticFilter all judge the same declaring site symbol.
        var filtersBySiteSymbol = accessibilityFilter.HasValue || kindFilter.HasValue || nameFilter != null
            || staticFilter.HasValue;

        // Site filters share one resolver so each file is bound at most once when several are set.
        SiteEnclosingSymbolResolver? siteResolver = null;
        if (filtersBySiteSymbol || !string.IsNullOrWhiteSpace(@params.NamespaceFilter))
            siteResolver = new SiteEnclosingSymbolResolver(Context);

        // Optional namespaceFilter (same normalization / matching as find_references / find_callers):
        // keep only diagnostics whose enclosing symbol (member, type, or namespace declaration) lives
        // in the filter namespace or one nested inside it. Null when omitted / blank / bare global::.
        var namespaceScope = NamespaceSiteScope.Create(Context, @params.NamespaceFilter, siteResolver);

        // Namespaces have no accessibility modifier but Roslyn reports INamespaceSymbol as Public (same
        // guard as find_references / search_symbols), so a namespace-level site (e.g. an unused using
        // inside a namespace declaration) never matches a named accessibility, kind, or name filter, nor
        // either staticFilter value.
        bool MatchesAccessibility(ISymbol? siteSymbol) =>
            !accessibilityFilter.HasValue
            || (siteSymbol is not null and not INamespaceSymbol
                && siteSymbol.DeclaredAccessibility == accessibilityFilter.Value);

        bool MatchesKind(ISymbol? siteSymbol) =>
            !kindFilter.HasValue
            || (siteSymbol is not null and not INamespaceSymbol
                && SymbolKindMapper.Map(siteSymbol) == kindFilter.Value);

        bool MatchesName(ISymbol? siteSymbol) =>
            nameFilter == null
            || (siteSymbol is not null and not INamespaceSymbol
                && NameFilterMatcher.Matches(siteSymbol.Name, nameFilter));

        bool MatchesStatic(ISymbol? siteSymbol) =>
            !staticFilter.HasValue
            || (siteSymbol is not null and not INamespaceSymbol
                && siteSymbol.IsStatic == staticFilter.Value);

        // Occurrence index of each in-source diagnostic among the diagnostics sharing its tree, id, and
        // span, counted in compilation order before any filter runs. Collocated parser diagnostics (two
        // zero-width CS1513 at the end of `public class C { private class D {`, one per missing `}`) are
        // equal values and Roslyn does not preserve Diagnostic identity, so the accessibility / kind / name site
        // of the k-th one is the carrier of the k-th such diagnostic in the tree (see
        // SiteEnclosingSymbolResolver.GetDeclaredOrEnclosingSymbolAsync).
        var siteOccurrences = new Dictionary<(SyntaxTree Tree, string Id, TextSpan Span), int>();

        foreach (var project in ProjectPathFilter.SelectProjects(Context.Solution, @params.ProjectPath))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            foreach (var diag in compilation.GetDiagnostics(cancellationToken))
            {
                var occurrence = 0;
                if (filtersBySiteSymbol && diag.Location.SourceTree is { } occurrenceTree)
                {
                    var occurrenceKey = (occurrenceTree, diag.Id, diag.Location.SourceSpan);
                    occurrence = siteOccurrences.GetValueOrDefault(occurrenceKey);
                    siteOccurrences[occurrenceKey] = occurrence + 1;
                }

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

                // The formatted message is needed for the result anyway; format it once and apply the
                // message filter before the (more expensive) site-based namespace / accessibility filters.
                var message = diag.GetMessage();
                if (!NameFilterMatcher.Matches(message, messageFilter))
                    continue;

                // Namespace / accessibility / kind / name / static filters run after the cheaper filters and before
                // maxResults so TotalCount / Truncated reflect the filtered set. Diagnostics with no
                // in-source location have no site and never match a named filter.
                if (namespaceScope != null)
                {
                    if (!diag.Location.IsInSource)
                        continue;

                    var tree = diag.Location.SourceTree;
                    if (!await namespaceScope.ContainsAsync(
                            tree,
                            tree == null ? null : Context.Solution.GetDocument(tree),
                            diag.Location.SourceSpan.Start,
                            cancellationToken))
                        continue;
                }

                if (filtersBySiteSymbol)
                {
                    if (!diag.Location.IsInSource)
                        continue;

                    var tree = diag.Location.SourceTree;
                    var site = await siteResolver!.GetDeclaredOrEnclosingSymbolAsync(
                        tree,
                        tree == null ? null : Context.Solution.GetDocument(tree),
                        diag.Location.SourceSpan,
                        diag.Id,
                        occurrence,
                        cancellationToken);
                    var declaringSite = DeclaringSiteSymbol.Resolve(site);
                    if (!MatchesAccessibility(declaringSite) || !MatchesKind(declaringSite) || !MatchesName(declaringSite)
                        || !MatchesStatic(declaringSite))
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
                    Message = message,
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

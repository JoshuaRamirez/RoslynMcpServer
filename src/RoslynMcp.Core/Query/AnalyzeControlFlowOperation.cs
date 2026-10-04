using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Analyzes control flow for a region of code using Roslyn's SemanticModel.AnalyzeControlFlow().
/// Honors optional <c>startColumn</c> / <c>endColumn</c> to trim the region span.
/// Omitted columns keep today's whole-line span (start of <c>startLine</c>
/// through end of <c>endLine</c>). Do not force column 1 when omitted.
/// Statement matching stays today's <c>span.Contains(s.Span)</c>.
/// </summary>
public sealed class AnalyzeControlFlowOperation : QueryOperationBase<AnalyzeControlFlowParams, AnalyzeControlFlowResult>
{
    /// <inheritdoc />
    public AnalyzeControlFlowOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AnalyzeControlFlowParams @params) => Validate(@params);

    /// <summary>
    /// Validates analyze-control-flow parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(AnalyzeControlFlowParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.StartLine > @params.EndLine)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startLine must be <= endLine.");

        if (@params.StartColumn.HasValue && @params.StartColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (@params.EndColumn.HasValue && @params.EndColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (@params.StartLine == @params.EndLine &&
            @params.StartColumn.HasValue &&
            @params.EndColumn.HasValue &&
            @params.StartColumn.Value > @params.EndColumn.Value)
        {
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startColumn must be <= endColumn when both are set on the same line.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<AnalyzeControlFlowResult>> ExecuteCoreAsync(
        Guid operationId,
        AnalyzeControlFlowParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var text = await document.GetTextAsync(cancellationToken);
        var span = BuildRegionSpan(text, @params);

        // Find statements in the region. Today's matching: the region's
        // span must fully contain the statement span. Do not invent a
        // covering-span / intersection fallback when nothing is contained.
        // Prefer statements that are direct children of ONE statement-list
        // parent (BlockSyntax / SwitchSectionSyntax). Parent-type alone
        // still admits siblings from nested lists (e.g. return 1 under
        // if (a) and return 2 under if (b)): each Parent is a different
        // BlockSyntax, so kind filter alone hands mixed parents to
        // ValidateStatementRange → ArgumentException → RoslynError (#936).
        // Among candidates, pick the outermost contained list parent
        // (parity with AnalyzeDataFlowOperation / #931) and keep only its
        // direct children so First/Last share a parent.
        // If that sibling-list filter yields empty and the region
        // contains exactly one StatementSyntax (e.g. unbraced embedded
        // return under if), analyze that single statement alone.
        var containedStatements = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => span.Contains(s.Span))
            .ToList();

        var listParentCandidates = containedStatements
            .Where(s => s.Parent is BlockSyntax or SwitchSectionSyntax)
            .ToList();

        var statements = listParentCandidates;
        if (listParentCandidates.Count > 0)
        {
            var parents = listParentCandidates
                .Select(s => s.Parent!)
                .Distinct()
                .ToList();

            // Outermost contained statement-list parent: contains every
            // other candidate parent (or is the only one).
            var outermostParent = parents.FirstOrDefault(p =>
                parents.All(other => other == p || p.Contains(other)));

            if (outermostParent != null)
            {
                // Direct children of the outermost list among candidates,
                // ordered by source position.
                var directChildren = listParentCandidates
                    .Where(s => s.Parent == outermostParent)
                    .OrderBy(s => s.SpanStart)
                    .ToList();

                // Nested candidates under outermostParent (different
                // statement lists). Accept outermost only when every
                // nested candidate falls within the span from the first
                // to last direct-child candidate — otherwise a middle
                // outer statement (e.g. Log()) can win while boundary
                // nested returns are filtered away → silent wrong
                // analysis of only Log() instead of InvalidRegion.
                var nestedCandidates = listParentCandidates
                    .Where(s => s.Parent != outermostParent && outermostParent.Contains(s))
                    .ToList();

                if (nestedCandidates.Count > 0)
                {
                    if (directChildren.Count == 0)
                    {
                        throw new RefactoringException(
                            ErrorCodes.InvalidRegion,
                            "Selected statements are not within the same statement list.");
                    }

                    var coverStart = directChildren.First().Span.Start;
                    var coverEnd = directChildren.Last().Span.End;
                    if (nestedCandidates.Any(n =>
                            n.Span.Start < coverStart || n.Span.End > coverEnd))
                    {
                        throw new RefactoringException(
                            ErrorCodes.InvalidRegion,
                            "Selected statements are not within the same statement list.");
                    }
                }

                statements = directChildren;
            }
            else
            {
                // No single parent contains the others (cross-list region).
                // Reject rather than hand mixed parents to Roslyn (#936).
                throw new RefactoringException(
                    ErrorCodes.InvalidRegion,
                    "Selected statements are not within the same statement list.");
            }
        }

        if (statements.Count == 0)
        {
            if (containedStatements.Count == 1)
                statements = containedStatements;
            else
                throw new RefactoringException(ErrorCodes.InvalidRegion, "No statements found in the specified region.");
        }

        // Get first and last statement for analysis (first==last for a
        // single-statement / embedded-statement region).
        var firstStatement = statements.First();
        var lastStatement = statements.Last();

        var controlFlowAnalysis = semanticModel.AnalyzeControlFlow(firstStatement, lastStatement);

        if (controlFlowAnalysis == null || !controlFlowAnalysis.Succeeded)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Control flow analysis failed for the specified region.");

        var returnStatements = new List<ControlFlowStatement>();
        var exitPoints = new List<ControlFlowStatement>();
        var entryPoints = new List<ControlFlowStatement>();

        foreach (var returnStmt in controlFlowAnalysis.ReturnStatements)
        {
            var lineSpan = returnStmt.GetLocation().GetLineSpan();
            returnStatements.Add(new ControlFlowStatement
            {
                Kind = "Return",
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                Text = returnStmt.ToString().Trim()
            });
        }

        foreach (var exitPoint in controlFlowAnalysis.ExitPoints)
        {
            var lineSpan = exitPoint.GetLocation().GetLineSpan();
            var kind = exitPoint switch
            {
                ReturnStatementSyntax => "Return",
                BreakStatementSyntax => "Break",
                ContinueStatementSyntax => "Continue",
                GotoStatementSyntax => "Goto",
                ThrowStatementSyntax => "Throw",
                ThrowExpressionSyntax => "Throw",
                _ => "Other"
            };

            exitPoints.Add(new ControlFlowStatement
            {
                Kind = kind,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                Text = exitPoint.ToString().Trim()
            });
        }

        foreach (var entryPoint in controlFlowAnalysis.EntryPoints)
        {
            var lineSpan = entryPoint.GetLocation().GetLineSpan();
            var kind = entryPoint switch
            {
                LabeledStatementSyntax => "Label",
                _ => "Other"
            };

            entryPoints.Add(new ControlFlowStatement
            {
                Kind = kind,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                Text = entryPoint.ToString().Trim()
            });
        }

        var result = new AnalyzeControlFlowResult
        {
            StartPointReachable = controlFlowAnalysis.StartPointIsReachable,
            EndPointReachable = controlFlowAnalysis.EndPointIsReachable,
            ReturnStatements = returnStatements,
            ExitPoints = exitPoints,
            EntryPoints = entryPoints
        };

        return QueryResult<AnalyzeControlFlowResult>.Succeeded(operationId, result);
    }

    /// <summary>
    /// Thin forwarder to <see cref="SyntaxLineHelpers.BuildRegionSpan"/>.
    /// Existing unit tests call this operation type.
    /// </summary>
    internal static TextSpan BuildRegionSpan(SourceText text, AnalyzeControlFlowParams @params) =>
        SyntaxLineHelpers.BuildRegionSpan(
            text,
            @params.StartLine,
            @params.EndLine,
            @params.StartColumn,
            @params.EndColumn);
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Analyzes control flow for a region of code using Roslyn's SemanticModel.AnalyzeControlFlow().
/// </summary>
public sealed class AnalyzeControlFlowOperation : QueryOperationBase<AnalyzeControlFlowParams, AnalyzeControlFlowResult>
{
    /// <inheritdoc />
    public AnalyzeControlFlowOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AnalyzeControlFlowParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.StartLine > @params.EndLine)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startLine must be <= endLine.");
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

        // Convert 1-based lines to 0-based
        var startLine = @params.StartLine - 1;
        var endLine = @params.EndLine - 1;

        if (startLine >= text.Lines.Count || endLine >= text.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Line range exceeds file length.");

        var startPosition = text.Lines[startLine].Start;
        var endPosition = text.Lines[endLine].End;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        // Find statements in the region
        var statements = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => span.Contains(s.Span))
            .ToList();

        if (statements.Count == 0)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "No statements found in the specified region.");

        // Get first and last statement for analysis
        var firstStatement = statements.First();
        var lastStatement = statements.Last();

        var controlFlowAnalysis = semanticModel.AnalyzeControlFlow(firstStatement, lastStatement);

        if (controlFlowAnalysis == null || !controlFlowAnalysis.Succeeded)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Control flow analysis failed for the specified region.");

        var returnStatements = new List<ControlFlowStatement>();
        var exitPoints = new List<ControlFlowStatement>();

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

        var result = new AnalyzeControlFlowResult
        {
            StartPointReachable = controlFlowAnalysis.StartPointIsReachable,
            EndPointReachable = controlFlowAnalysis.EndPointIsReachable,
            ReturnStatements = returnStatements,
            ExitPoints = exitPoints
        };

        return QueryResult<AnalyzeControlFlowResult>.Succeeded(operationId, result);
    }
}

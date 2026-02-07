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
/// Analyzes data flow for a region of code using Roslyn's SemanticModel.AnalyzeDataFlow().
/// </summary>
public sealed class AnalyzeDataFlowOperation : QueryOperationBase<AnalyzeDataFlowParams, AnalyzeDataFlowResult>
{
    /// <inheritdoc />
    public AnalyzeDataFlowOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AnalyzeDataFlowParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.StartLine > @params.EndLine)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startLine must be <= endLine.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<AnalyzeDataFlowResult>> ExecuteCoreAsync(
        Guid operationId,
        AnalyzeDataFlowParams @params,
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

        var firstStatement = statements.First();
        var lastStatement = statements.Last();

        var dataFlowAnalysis = semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);

        if (dataFlowAnalysis == null || !dataFlowAnalysis.Succeeded)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Data flow analysis failed for the specified region.");

        var result = new AnalyzeDataFlowResult
        {
            ReadInside = dataFlowAnalysis.ReadInside.Select(s => s.Name).ToList(),
            WrittenInside = dataFlowAnalysis.WrittenInside.Select(s => s.Name).ToList(),
            DataFlowsIn = dataFlowAnalysis.DataFlowsIn.Select(s => s.Name).ToList(),
            DataFlowsOut = dataFlowAnalysis.DataFlowsOut.Select(s => s.Name).ToList(),
            Captured = dataFlowAnalysis.Captured.Select(s => s.Name).ToList(),
            AlwaysAssigned = dataFlowAnalysis.AlwaysAssigned.Select(s => s.Name).ToList()
        };

        return QueryResult<AnalyzeDataFlowResult>.Succeeded(operationId, result);
    }
}

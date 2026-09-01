using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Analyzes data flow for a region of code using Roslyn's SemanticModel.AnalyzeDataFlow().
/// Honors optional <c>startColumn</c> / <c>endColumn</c> to trim the region span.
/// Omitted columns keep today's whole-line span (start of <c>startLine</c>
/// through end of <c>endLine</c>). Do not force column 1 when omitted.
/// Statement matching stays today's <c>span.Contains(s.Span)</c>.
/// </summary>
public sealed class AnalyzeDataFlowOperation : QueryOperationBase<AnalyzeDataFlowParams, AnalyzeDataFlowResult>
{
    /// <inheritdoc />
    public AnalyzeDataFlowOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AnalyzeDataFlowParams @params) => Validate(@params);

    /// <summary>
    /// Validates analyze-data-flow parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(AnalyzeDataFlowParams @params)
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
        var span = BuildRegionSpan(text, @params);

        // Find statements in the region. Today's matching: the region's
        // span must fully contain the statement span. Do not invent a
        // covering-span / intersection fallback when nothing is contained.
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

    /// <summary>
    /// Builds the analysis region span. Omitted columns keep today's
    /// whole-line span (start of <paramref name="params"/>.<c>StartLine</c>
    /// through <c>TextLine.End</c> of <c>EndLine</c>). Set
    /// <c>startColumn</c> uses that 1-based column on the start line
    /// (Roslyn <c>Character = column - 1</c>). Set <c>endColumn</c> uses
    /// that 1-based column on the end line. One omitted, the other set:
    /// omitted start stays start-of-line; omitted end stays end-of-line.
    /// Combined span must be start &lt;= end in absolute positions.
    /// </summary>
    internal static TextSpan BuildRegionSpan(SourceText text, AnalyzeDataFlowParams @params)
    {
        // Convert 1-based lines to 0-based
        var startLine = @params.StartLine - 1;
        var endLine = @params.EndLine - 1;

        if (startLine >= text.Lines.Count || endLine >= text.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Line range exceeds file length.");

        var startLineInfo = text.Lines[startLine];
        var endLineInfo = text.Lines[endLine];

        // Omitted startColumn: today's start of startLine. Set: that
        // 1-based column on startLine (Character = column - 1). Do not
        // force column 1 when omitted. A column past TextLine.End would
        // leak into later lines — reject as InvalidColumnNumber (same
        // line-local bound as SymbolResolver / ExtractMethod).
        var startPosition = startLineInfo.Start;
        if (@params.StartColumn is int startColumn)
            startPosition = GetColumnPosition(startLineInfo, @params.StartLine, startColumn);

        // Omitted endColumn: today's TextLine.End of endLine (exclusive-ish
        // of the line break). Set: that 1-based column on endLine.
        var endPosition = endLineInfo.End;
        if (@params.EndColumn is int endColumn)
            endPosition = GetColumnPosition(endLineInfo, @params.EndLine, endColumn);

        if (startPosition < 0 || endPosition < 0 ||
            startPosition > text.Length || endPosition > text.Length ||
            startPosition > endPosition)
        {
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Region start must be <= end.");
        }

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    /// <summary>
    /// Converts a 1-based column on <paramref name="lineInfo"/> to an
    /// absolute position. Valid columns are 1 through
    /// <c>lineLength + 1</c> (the exclusive <see cref="TextLine.End"/>).
    /// Past that would cross the line break into later lines.
    /// </summary>
    private static int GetColumnPosition(TextLine lineInfo, int lineNumber, int column)
    {
        var columnIndex = column - 1;
        var lineLength = lineInfo.End - lineInfo.Start;
        if (columnIndex < 0 || columnIndex > lineLength)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidColumnNumber,
                $"Column {column} is out of range for line {lineNumber} (line has {lineLength} characters).");
        }

        return lineInfo.Start + columnIndex;
    }
}

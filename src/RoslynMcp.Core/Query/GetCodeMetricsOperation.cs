using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Computes code metrics (cyclomatic complexity, LOC, maintainability index,
/// class coupling, depth of inheritance) for a symbol or file.
/// </summary>
public sealed class GetCodeMetricsOperation : QueryOperationBase<GetCodeMetricsParams, GetCodeMetricsResult>
{
    /// <inheritdoc />
    public GetCodeMetricsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GetCodeMetricsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile) && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either sourceFile or symbolName is required.");

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            if (!PathResolver.IsAbsolutePath(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        }

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!string.IsNullOrWhiteSpace(@params.SourceFile) && !File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetCodeMetricsResult>> ExecuteCoreAsync(
        Guid operationId,
        GetCodeMetricsParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        SyntaxNode targetNode;
        string symbolName;
        string fqn;
        INamedTypeSymbol? typeSymbol = null;

        if (!string.IsNullOrWhiteSpace(@params.SymbolName) || @params.Line.HasValue)
        {
            // Resolve specific symbol
            var resolved = await SymbolResolver.ResolveSymbolAsync(
                @params.SourceFile!, @params.SymbolName, @params.Line, null, cancellationToken);

            var symbol = resolved.Symbol;
            symbolName = symbol.Name;
            fqn = symbol.ToDisplayString();

            // Find the syntax node for this symbol
            var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
                throw new RefactoringException(ErrorCodes.SymbolNotFound, "Symbol has no syntax declaration.");

            targetNode = await syntaxRef.GetSyntaxAsync(cancellationToken);
            typeSymbol = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        }
        else
        {
            // Analyze entire file
            targetNode = root;
            symbolName = System.IO.Path.GetFileName(@params.SourceFile!);
            fqn = @params.SourceFile!;

            // Try to find the primary type in the file
            var firstType = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (firstType != null)
            {
                typeSymbol = semanticModel.GetDeclaredSymbol(firstType, cancellationToken) as INamedTypeSymbol;
            }
        }

        var cc = MetricsCalculator.CalculateCyclomaticComplexity(targetNode);
        var loc = MetricsCalculator.CalculateLinesOfCode(targetNode);
        var mi = MetricsCalculator.CalculateMaintainabilityIndex(cc, loc);
        var coupling = MetricsCalculator.CalculateClassCoupling(semanticModel, targetNode);
        var doi = MetricsCalculator.CalculateDepthOfInheritance(typeSymbol);

        var result = new GetCodeMetricsResult
        {
            SymbolName = symbolName,
            FullyQualifiedName = fqn,
            CyclomaticComplexity = cc,
            LinesOfCode = loc,
            MaintainabilityIndex = mi,
            ClassCoupling = coupling,
            DepthOfInheritance = doi
        };

        return QueryResult<GetCodeMetricsResult>.Succeeded(operationId, result);
    }
}

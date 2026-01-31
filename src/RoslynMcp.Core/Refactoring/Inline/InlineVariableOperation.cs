using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// Inlines a local variable by replacing all usages with its initializer value.
/// </summary>
public sealed class InlineVariableOperation : RefactoringOperationBase<InlineVariableParams>
{
    /// <summary>
    /// Creates a new inline variable operation.
    /// </summary>
    public InlineVariableOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(InlineVariableParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        InlineVariableParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find variable declaration
        var variableDeclarators = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.Text == @params.VariableName)
            .ToList();

        if (variableDeclarators.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.VariableNotFound,
                $"Variable '{@params.VariableName}' not found.");
        }

        VariableDeclaratorSyntax declarator;
        if (variableDeclarators.Count > 1)
        {
            if (!@params.Line.HasValue)
            {
                var lines = variableDeclarators
                    .Select(v => v.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                    .ToList();
                throw new RefactoringException(
                    ErrorCodes.SymbolAmbiguous,
                    $"Multiple variables named '{@params.VariableName}' found. Provide line number. Options: {string.Join(", ", lines)}");
            }

            declarator = variableDeclarators.FirstOrDefault(v =>
                v.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value)
                ?? throw new RefactoringException(
                    ErrorCodes.VariableNotFound,
                    $"Variable '{@params.VariableName}' not found at line {@params.Line}.");
        }
        else
        {
            declarator = variableDeclarators[0];
        }

        // Check for initializer
        if (declarator.Initializer == null)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Variable must have an initializer to be inlined.");
        }

        var initializerExpression = declarator.Initializer.Value;

        // Check for side effects in initializer
        if (!MemberAnalyzer.IsSafeToInline(initializerExpression, semanticModel))
        {
            throw new RefactoringException(
                ErrorCodes.CannotInlineSideEffects,
                "Cannot inline expression with potential side effects (method calls, object creation, etc.).");
        }

        // Get variable symbol
        var variableSymbol = semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
        if (variableSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve variable symbol.");
        }

        // Find all usages
        var containingMethod = declarator.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Variable must be inside a method.");
        }

        var usages = containingMethod.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id =>
            {
                if (id.Identifier.Text != @params.VariableName) return false;

                // Check it's the same symbol
                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                return SymbolEqualityComparer.Default.Equals(symbol, variableSymbol);
            })
            .ToList();

        // Check for ref/out usage
        foreach (var usage in usages)
        {
            var parent = usage.Parent;
            if (parent is ArgumentSyntax arg)
            {
                if (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
                    arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword))
                {
                    throw new RefactoringException(
                        ErrorCodes.UsedInRefContext,
                        "Cannot inline variable used in ref/out context.");
                }
            }
        }

        // Check for assignments to the variable
        var assignments = containingMethod.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a =>
            {
                if (a.Left is not IdentifierNameSyntax id) return false;
                if (id.Identifier.Text != @params.VariableName) return false;

                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                return SymbolEqualityComparer.Default.Equals(symbol, variableSymbol);
            })
            .ToList();

        if (assignments.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MultipleAssignments,
                "Cannot inline variable that is reassigned.");
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, initializerExpression, usages.Count);
        }

        // Apply changes: replace all usages with initializer, remove declaration
        var rewriter = new InlineRewriter(@params.VariableName, variableSymbol, initializerExpression, semanticModel);
        var newRoot = rewriter.Visit(root);

        // Remove the declaration statement if it only declares this variable
        var declarationStatement = declarator.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var variableDeclaration = declarator.Parent as VariableDeclarationSyntax;

        if (variableDeclaration != null && variableDeclaration.Variables.Count == 1)
        {
            // Remove entire statement
            var statementToRemove = newRoot.DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .FirstOrDefault(s => s.Declaration.Variables.Any(v => v.Identifier.Text == @params.VariableName));
            if (statementToRemove != null)
            {
                newRoot = newRoot.RemoveNode(statementToRemove, SyntaxRemoveOptions.KeepLeadingTrivia);
            }
        }
        else
        {
            // Just remove this variable from the declaration
            var newDeclarator = newRoot.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.Text == @params.VariableName);
            if (newDeclarator != null)
            {
                newRoot = newRoot.RemoveNode(newDeclarator, SyntaxRemoveOptions.KeepNoTrivia);
            }
        }

        var newDocument = document.WithSyntaxRoot(newRoot!);
        var newSolution = newDocument.Project.Solution;

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            new Contracts.Models.SymbolInfo
            {
                Name = @params.VariableName,
                FullyQualifiedName = @params.VariableName,
                Kind = Contracts.Enums.SymbolKind.Local
            },
            usages.Count,
            0);
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        InlineVariableParams @params,
        ExpressionSyntax initializer,
        int usageCount)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Inline variable '{@params.VariableName}' ({usageCount} usages replaced)",
                BeforeSnippet = $"var {@params.VariableName} = {initializer.ToFullString().Trim()};\n// ... {@params.VariableName} ...",
                AfterSnippet = $"// (declaration removed)\n// ... {initializer.ToFullString().Trim()} ..."
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed class InlineRewriter : CSharpSyntaxRewriter
    {
        private readonly string _variableName;
        private readonly ILocalSymbol _variableSymbol;
        private readonly ExpressionSyntax _replacement;
        private readonly SemanticModel _semanticModel;

        public InlineRewriter(
            string variableName,
            ILocalSymbol variableSymbol,
            ExpressionSyntax replacement,
            SemanticModel semanticModel)
        {
            _variableName = variableName;
            _variableSymbol = variableSymbol;
            _replacement = replacement;
            _semanticModel = semanticModel;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.Text != _variableName)
            {
                return base.VisitIdentifierName(node);
            }

            // Check it's the same symbol
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(symbol, _variableSymbol))
            {
                return base.VisitIdentifierName(node);
            }

            // Don't replace the declaration itself
            if (node.Parent is VariableDeclaratorSyntax ||
                node.Parent?.Parent is VariableDeclaratorSyntax)
            {
                return base.VisitIdentifierName(node);
            }

            // Wrap in parentheses if needed for precedence
            var needsParens = node.Parent is BinaryExpressionSyntax ||
                              node.Parent is MemberAccessExpressionSyntax ||
                              node.Parent is ConditionalExpressionSyntax;

            if (needsParens && _replacement is BinaryExpressionSyntax or ConditionalExpressionSyntax)
            {
                return SyntaxFactory.ParenthesizedExpression(_replacement)
                    .WithTriviaFrom(node);
            }

            return _replacement.WithTriviaFrom(node);
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts an expression to a local variable.
/// </summary>
public sealed class ExtractVariableOperation : RefactoringOperationBase<ExtractVariableParams>
{
    /// <summary>
    /// Creates a new extract variable operation.
    /// </summary>
    public ExtractVariableOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractVariableParams @params)
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

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.StartColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (@params.EndLine < @params.StartLine ||
            (@params.EndLine == @params.StartLine && @params.EndColumn < @params.StartColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        if (!IsValidIdentifier(@params.VariableName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid variable name: {@params.VariableName}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractVariableParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Get text span from line/column
        var sourceText = await document.GetTextAsync(cancellationToken);
        var startPosition = sourceText.Lines[@params.StartLine - 1].Start + @params.StartColumn - 1;
        var endPosition = sourceText.Lines[@params.EndLine - 1].Start + @params.EndColumn - 1;
        var span = TextSpan.FromBounds(startPosition, endPosition);

        // Find expression at span
        var node = root.FindNode(span);
        var expression = FindEnclosingExpression(node, span);

        if (expression == null)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFound,
                "No valid expression found at the specified location.");
        }

        // Get expression type
        var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
        if (typeInfo.Type == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                "Could not determine expression type.");
        }

        // Check for void
        if (typeInfo.Type.SpecialType == SpecialType.System_Void)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionIsVoid,
                "Cannot extract void expression to variable.");
        }

        // Find containing statement
        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            throw new RefactoringException(
                ErrorCodes.StatementNotFound,
                "Expression must be inside a statement.");
        }

        // Check for existing variable with same name in scope
        var containingBlock = containingStatement.Parent as BlockSyntax;
        if (containingBlock != null)
        {
            var existingVar = containingBlock.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.Text == @params.VariableName);

            if (existingVar != null && existingVar.SpanStart < expression.SpanStart)
            {
                throw new RefactoringException(
                    ErrorCodes.NameCollision,
                    $"Variable '{@params.VariableName}' already exists in scope.");
            }
        }

        // Determine type syntax
        TypeSyntax typeSyntax;
        if (@params.UseVar || typeInfo.Type.IsAnonymousType)
        {
            typeSyntax = SyntaxFactory.IdentifierName("var");
        }
        else
        {
            typeSyntax = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
        }

        // Create variable declaration
        var variableDeclaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(typeSyntax.WithTrailingTrivia(SyntaxFactory.Space))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(@params.VariableName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(expression)))));

        // Create variable reference
        var variableRef = SyntaxFactory.IdentifierName(@params.VariableName);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, expression, typeInfo.Type, variableDeclaration);
        }

        // Apply changes: insert declaration before statement, replace expression with variable
        var newStatements = new List<StatementSyntax>();

        // Add declaration
        newStatements.Add(variableDeclaration.NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        // Modify original statement to use variable
        var newStatement = containingStatement.ReplaceNode(expression, variableRef);
        newStatements.Add(newStatement);

        // Replace the containing statement with new statements
        SyntaxNode newRoot;
        if (containingBlock != null)
        {
            var statementIndex = containingBlock.Statements.IndexOf(containingStatement);
            var newBlockStatements = containingBlock.Statements
                .Take(statementIndex)
                .Concat(newStatements)
                .Concat(containingBlock.Statements.Skip(statementIndex + 1))
                .ToList();

            var newBlock = containingBlock.WithStatements(SyntaxFactory.List(newBlockStatements));
            newRoot = root.ReplaceNode(containingBlock, newBlock);
        }
        else
        {
            // Single statement context (like expression-bodied member)
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot extract variable outside of a block statement.");
        }

        var newDocument = document.WithSyntaxRoot(newRoot);
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
            0,
            0);
    }

    private static ExpressionSyntax? FindEnclosingExpression(SyntaxNode node, TextSpan span)
    {
        // Walk up to find the smallest expression that contains the span
        var current = node;
        ExpressionSyntax? bestMatch = null;

        while (current != null)
        {
            if (current is ExpressionSyntax expr && current.Span.Contains(span))
            {
                // Prefer expressions that more closely match the selection
                if (bestMatch == null || current.Span.Length <= bestMatch.Span.Length)
                {
                    bestMatch = expr;
                }
            }
            current = current.Parent;
        }

        // Avoid extracting entire statements as expressions
        if (bestMatch?.Parent is ExpressionStatementSyntax)
        {
            return bestMatch;
        }

        return bestMatch;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractVariableParams @params,
        ExpressionSyntax expression,
        ITypeSymbol type,
        LocalDeclarationStatementSyntax declaration)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Extract expression to variable '{@params.VariableName}' of type {type.ToDisplayString()}",
                BeforeSnippet = expression.ToFullString(),
                AfterSnippet = $"{declaration.NormalizeWhitespace()}\n// ... {@params.VariableName} used in place of expression"
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

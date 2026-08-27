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

        if (@params.ReplaceAll && HasSideEffects(expression, semanticModel, cancellationToken))
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionHasSideEffects,
                "Cannot replace all occurrences of an expression with side effects.");
        }

        var replacements = @params.ReplaceAll
            ? FindEquivalentExpressions(expression, semanticModel, cancellationToken)
            : new List<ExpressionSyntax> { expression };

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

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, expression, typeInfo.Type, variableDeclaration, replacements.Count);
        }

        SyntaxNode newRoot;
        if (replacements.Count <= 1)
        {
            newRoot = ApplySingleReplacement(root, expression, containingStatement, containingBlock, @params.VariableName, variableDeclaration);
        }
        else
        {
            newRoot = ApplyReplaceAll(root, replacements, @params.VariableName, variableDeclaration);
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

    private static SyntaxNode ApplySingleReplacement(
        SyntaxNode root,
        ExpressionSyntax expression,
        StatementSyntax containingStatement,
        BlockSyntax? containingBlock,
        string variableName,
        LocalDeclarationStatementSyntax variableDeclaration)
    {
        var variableRef = SyntaxFactory.IdentifierName(variableName);
        var newStatements = new List<StatementSyntax>
        {
            variableDeclaration.NormalizeWhitespace()
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed),
            containingStatement.ReplaceNode(expression, variableRef)
        };

        if (containingBlock != null)
        {
            var statementIndex = containingBlock.Statements.IndexOf(containingStatement);
            var newBlockStatements = containingBlock.Statements
                .Take(statementIndex)
                .Concat(newStatements)
                .Concat(containingBlock.Statements.Skip(statementIndex + 1))
                .ToList();

            var newBlock = containingBlock.WithStatements(SyntaxFactory.List(newBlockStatements));
            return root.ReplaceNode(containingBlock, newBlock);
        }

        throw new RefactoringException(
            ErrorCodes.InvalidSelection,
            "Cannot extract variable outside of a block statement.");
    }

    private static SyntaxNode ApplyReplaceAll(
        SyntaxNode root,
        IReadOnlyList<ExpressionSyntax> replacements,
        string variableName,
        LocalDeclarationStatementSyntax variableDeclaration)
    {
        var first = replacements.OrderBy(e => e.SpanStart).First();
        var insertionBlock = GetInnermostBlock(first)
            ?? throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot extract variable outside of a block statement.");
        var insertBefore = insertionBlock.Statements.FirstOrDefault(s => s.Span.Contains(first.Span))
            ?? throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot extract variable outside of a block statement.");
        var insertIndex = insertionBlock.Statements.IndexOf(insertBefore);

        var existingVar = insertionBlock.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == variableName);
        if (existingVar != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Variable '{variableName}' already exists in scope.");
        }

        // Annotate replacements and the insertion block in separate passes.
        // ReplaceNodes skips descendants when an ancestor is in the same batch.
        var replaceAnn = new SyntaxAnnotation("extract-variable-replace");
        var blockAnn = new SyntaxAnnotation("extract-variable-block");

        var withReplacements = root.ReplaceNodes(
            replacements,
            (original, _) => original.WithAdditionalAnnotations(replaceAnn));

        var annotatedFirst = withReplacements.GetAnnotatedNodes(replaceAnn)
            .OfType<ExpressionSyntax>()
            .OrderBy(e => e.SpanStart)
            .First();
        var blockToAnnotate = GetInnermostBlock(annotatedFirst)
            ?? throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot extract variable outside of a block statement.");
        var withBlock = withReplacements.ReplaceNode(
            blockToAnnotate,
            blockToAnnotate.WithAdditionalAnnotations(blockAnn));

        var variableRef = SyntaxFactory.IdentifierName(variableName);
        var replaced = withBlock.ReplaceNodes(
            withBlock.GetAnnotatedNodes(replaceAnn),
            (original, _) => variableRef.WithTriviaFrom(original));

        var updatedBlock = replaced.GetAnnotatedNodes(blockAnn).OfType<BlockSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to locate insertion block after rewrite.");

        var declaration = variableDeclaration.NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        var newBlock = updatedBlock.WithStatements(updatedBlock.Statements.Insert(insertIndex, declaration));
        return replaced.ReplaceNode(updatedBlock, newBlock);
    }

    private static List<ExpressionSyntax> FindEquivalentExpressions(
        ExpressionSyntax original,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var block = GetInnermostBlock(original);
        if (block == null)
            return new List<ExpressionSyntax> { original };

        var originalCore = Unwrap(original);
        var originalBindings = CollectBindings(originalCore, semanticModel, cancellationToken);

        var matches = block.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(expr =>
            {
                if (GetInnermostBlock(expr) != block)
                    return false;

                var core = Unwrap(expr);
                if (core != expr)
                    return false;

                if (core == originalCore)
                    return true;

                return SyntaxFactory.AreEquivalent(core, originalCore) &&
                       BindingsEqual(originalBindings, CollectBindings(core, semanticModel, cancellationToken));
            })
            .ToList();

        if (!matches.Contains(originalCore) && !matches.Contains(original))
            matches.Add(original);

        return FilterByInterveningWrites(matches, original, originalBindings, semanticModel, cancellationToken);
    }

    private static List<ExpressionSyntax> FilterByInterveningWrites(
        List<ExpressionSyntax> matches,
        ExpressionSyntax original,
        IReadOnlyList<ISymbol?> bindings,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var sorted = matches.Distinct().OrderBy(e => e.SpanStart).ToList();
        var originalCore = Unwrap(original);
        var originalIndex = sorted.FindIndex(e => e == original || e == originalCore);
        if (originalIndex < 0)
        {
            sorted.Insert(0, original);
            originalIndex = 0;
        }

        var kept = new List<ExpressionSyntax> { sorted[originalIndex] };

        for (var i = originalIndex - 1; i >= 0; i--)
        {
            if (HasInterveningWrite(sorted[i], originalCore, bindings, semanticModel, cancellationToken))
                break;

            kept.Add(sorted[i]);
        }

        var leftmost = kept.OrderBy(e => e.SpanStart).First();
        for (var i = originalIndex + 1; i < sorted.Count; i++)
        {
            if (HasInterveningWrite(leftmost, sorted[i], bindings, semanticModel, cancellationToken))
                break;

            kept.Add(sorted[i]);
        }

        return kept.OrderBy(e => e.SpanStart).ToList();
    }

    private static bool HasInterveningWrite(
        ExpressionSyntax earlier,
        ExpressionSyntax later,
        IReadOnlyList<ISymbol?> bindings,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var symbol in bindings)
        {
            if (symbol != null)
                symbols.Add(symbol);
        }
        if (symbols.Count == 0)
            return false;

        var start = earlier.Span.End;
        var end = later.Span.Start;
        if (end <= start)
            return false;

        var root = earlier.SyntaxTree.GetRoot(cancellationToken);
        foreach (var node in root.DescendantNodes())
        {
            if (node.SpanStart < start || node.SpanStart >= end)
                continue;

            var written = GetWrittenSymbol(node, semanticModel, cancellationToken);
            if (written != null && symbols.Contains(written))
                return true;
        }

        return false;
    }

    private static ISymbol? GetWrittenSymbol(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        switch (node)
        {
            case AssignmentExpressionSyntax assignment:
                return GetAssignedSymbol(assignment.Left, semanticModel, cancellationToken);
            case PrefixUnaryExpressionSyntax prefix
                when prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                     prefix.IsKind(SyntaxKind.PreDecrementExpression):
                return GetAssignedSymbol(prefix.Operand, semanticModel, cancellationToken);
            case PostfixUnaryExpressionSyntax postfix
                when postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                     postfix.IsKind(SyntaxKind.PostDecrementExpression):
                return GetAssignedSymbol(postfix.Operand, semanticModel, cancellationToken);
            case ArgumentSyntax argument
                when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
                     argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                return GetAssignedSymbol(argument.Expression, semanticModel, cancellationToken);
            default:
                return null;
        }
    }

    private static ISymbol? GetAssignedSymbol(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var core = Unwrap(expression);
        return semanticModel.GetSymbolInfo(core, cancellationToken).Symbol;
    }

    private static BlockSyntax? GetInnermostBlock(SyntaxNode node) =>
        node.Ancestors().OfType<BlockSyntax>().FirstOrDefault();

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private static IReadOnlyList<ISymbol?> CollectBindings(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        return expression.DescendantNodesAndSelf()
            .OfType<SimpleNameSyntax>()
            .Select(name => semanticModel.GetSymbolInfo(name, cancellationToken).Symbol)
            .ToList();
    }

    private static bool BindingsEqual(IReadOnlyList<ISymbol?> left, IReadOnlyList<ISymbol?> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static bool HasSideEffects(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax:
                case AssignmentExpressionSyntax:
                case AwaitExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case ArrayCreationExpressionSyntax:
                case ImplicitArrayCreationExpressionSyntax:
                case StackAllocArrayCreationExpressionSyntax:
                    return true;
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                         postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return true;
                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                         prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    return true;
            }

            if (node is ExpressionSyntax expr)
            {
                var conversion = semanticModel.GetConversion(expr, cancellationToken);
                if (conversion.IsUserDefined)
                    return true;
            }

            var symbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
            if (symbol is IPropertySymbol)
                return true;

            if (symbol is IMethodSymbol method &&
                method.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
            {
                return true;
            }
        }

        return false;
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
        LocalDeclarationStatementSyntax declaration,
        int replacementCount)
    {
        var replacementSuffix = replacementCount > 1
            ? $" ({replacementCount} replacements)"
            : string.Empty;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Extract expression to variable '{@params.VariableName}' of type {type.ToDisplayString()}{replacementSuffix}",
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

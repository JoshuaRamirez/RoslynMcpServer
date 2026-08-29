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

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Flips an if-statement condition and swaps the if/else branches,
/// preserving semantics (UC-A4 invert_if).
/// </summary>
public sealed class InvertIfOperation : RefactoringOperationBase<InvertIfParams>
{
    /// <summary>
    /// Creates a new invert-if operation.
    /// </summary>
    public InvertIfOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(InvertIfParams @params) => Validate(@params);

    /// <summary>
    /// Validates invert-if parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(InvertIfParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <summary>
    /// Rejects documents that cannot receive source edits.
    /// </summary>
    internal static void ValidateDocumentIsEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (source-generated).");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable.");
        }

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        InvertIfParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var ifStatement = FindIfStatement(root, @params.Line, @params.Column);
        if (ifStatement == null)
        {
            throw new RefactoringException(
                ErrorCodes.NoIfStatementAtLocation,
                $"No if statement found at line {@params.Line}" +
                (@params.Column.HasValue ? $", column {@params.Column.Value}" : "") +
                ".");
        }

        if (ifStatement.Condition.IsMissing || ifStatement.Statement.IsMissing)
        {
            throw new RefactoringException(
                ErrorCodes.ConditionNotInvertible,
                "If statement is incomplete and cannot be safely inverted.");
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        var invertedCondition = InvertCondition(ifStatement.Condition, model);
        var invertedIf = RewriteIf(ifStatement, invertedCondition);

        var originalText = FormatCondition(ifStatement.Condition);
        var invertedText = FormatCondition(invertedCondition);
        var beforeSnippet = ifStatement.NormalizeWhitespace().ToFullString().Trim();
        var afterSnippet = invertedIf.NormalizeWhitespace().ToFullString().Trim();
        var description = BuildDescription(originalText, invertedText, ifStatement.Else == null);

        if (@params.Preview)
        {
            var span = ifStatement.GetLocation().GetLineSpan();
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = description,
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet,
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                }
            };

            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = pendingChanges,
                OriginalCondition = originalText,
                InvertedCondition = invertedText
            };
        }

        var newRoot = root.ReplaceNode(ifStatement, invertedIf);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            OriginalCondition = originalText,
            InvertedCondition = invertedText
        };
    }

    internal static IfStatementSyntax? FindIfStatement(SyntaxNode root, int line, int? column)
    {
        var onLine = root.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(statement => KeywordIsOnLine(statement, line))
            .ToList();

        if (onLine.Count == 0)
            return null;

        if (column.HasValue)
        {
            var atColumn = onLine
                .Where(statement => KeywordCoversColumn(statement, line, column.Value))
                .OrderBy(statement => statement.Span.Length)
                .ToList();
            return atColumn.FirstOrDefault();
        }

        return onLine.OrderBy(statement => statement.IfKeyword.SpanStart).First();
    }

    private static bool KeywordIsOnLine(IfStatementSyntax statement, int line)
    {
        var span = statement.IfKeyword.GetLocation().GetLineSpan();
        return span.StartLinePosition.Line + 1 == line;
    }

    private static bool KeywordCoversColumn(IfStatementSyntax statement, int line, int column)
    {
        var span = statement.IfKeyword.GetLocation().GetLineSpan();
        return SpanCoversColumn(span, line, column);
    }

    private static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column > endCol)
            return false;
        return true;
    }

    internal static ExpressionSyntax InvertCondition(ExpressionSyntax condition, SemanticModel? model)
    {
        if (IntroducesBoundVariable(condition))
        {
            throw new RefactoringException(
                ErrorCodes.ConditionNotInvertible,
                "Condition cannot be safely inverted because it introduces a pattern or out variable.");
        }

        return InvertExpression(condition, model);
    }

    private static IfStatementSyntax RewriteIf(IfStatementSyntax ifStatement, ExpressionSyntax invertedCondition)
    {
        var originalThen = ifStatement.Statement;
        var originalElse = ifStatement.Else?.Statement;

        StatementSyntax newThen;
        ElseClauseSyntax newElse;

        if (originalElse == null)
        {
            // AF-A4.01: empty if body, original then becomes else.
            newThen = SyntaxFactory.Block();
            newElse = SyntaxFactory.ElseClause(originalThen);
        }
        else
        {
            newThen = originalElse;
            newElse = SyntaxFactory.ElseClause(originalThen);
        }

        return ifStatement
            .WithCondition(invertedCondition.WithTriviaFrom(ifStatement.Condition))
            .WithStatement(newThen)
            .WithElse(newElse)
            .NormalizeWhitespace();
    }

    private static ExpressionSyntax InvertExpression(ExpressionSyntax expression, SemanticModel? model)
    {
        expression = expression.WithoutTrivia();

        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return SyntaxFactory.ParenthesizedExpression(
                    InvertExpression(parenthesized.Expression, model));

            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not:
                return not.Operand.WithoutTrivia();

            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.TrueLiteralExpression }:
                return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression);

            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.FalseLiteralExpression }:
                return SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression):
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalOrExpression,
                    ParenthesizeForLogicalOperand(InvertExpression(binary.Left, model)),
                    ParenthesizeForLogicalOperand(InvertExpression(binary.Right, model)));

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression):
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalAndExpression,
                    ParenthesizeForLogicalOperand(InvertExpression(binary.Left, model)),
                    ParenthesizeForLogicalOperand(InvertExpression(binary.Right, model)));

            case BinaryExpressionSyntax binary when TryFlipComparison(binary, model, out var flipped):
                return flipped;

            case IsPatternExpressionSyntax isPattern:
                return InvertIsPattern(isPattern);
        }

        return Negate(expression);
    }

    private static bool TryFlipComparison(
        BinaryExpressionSyntax binary,
        SemanticModel? model,
        out ExpressionSyntax flipped)
    {
        flipped = null!;
        var newKind = binary.Kind() switch
        {
            SyntaxKind.GreaterThanExpression => SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.LessThanExpression => SyntaxKind.GreaterThanOrEqualExpression,
            SyntaxKind.GreaterThanOrEqualExpression => SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanOrEqualExpression => SyntaxKind.GreaterThanExpression,
            SyntaxKind.EqualsExpression => SyntaxKind.NotEqualsExpression,
            SyntaxKind.NotEqualsExpression => SyntaxKind.EqualsExpression,
            _ => (SyntaxKind?)null
        };

        if (newKind == null)
            return false;

        if (model != null && !IsSafeToFlipComparison(binary, model))
            return false;

        flipped = SyntaxFactory.BinaryExpression(
            newKind.Value,
            binary.Left.WithoutTrivia(),
            binary.Right.WithoutTrivia());
        return true;
    }

    private static bool IsSafeToFlipComparison(BinaryExpressionSyntax binary, SemanticModel model)
    {
        if (model.GetSymbolInfo(binary).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            return false;

        var leftType = model.GetTypeInfo(binary.Left).Type;
        var rightType = model.GetTypeInfo(binary.Right).Type;
        return !IsFloatingPoint(leftType) && !IsFloatingPoint(rightType);
    }

    private static bool IsFloatingPoint(ITypeSymbol? type)
    {
        if (type == null)
            return false;

        return type.SpecialType is SpecialType.System_Single or SpecialType.System_Double;
    }

    private static ExpressionSyntax InvertIsPattern(IsPatternExpressionSyntax isPattern)
    {
        if (HasVariableDesignation(isPattern.Pattern))
        {
            throw new RefactoringException(
                ErrorCodes.ConditionNotInvertible,
                "Condition cannot be safely inverted because it introduces a pattern variable.");
        }

        if (isPattern.Pattern is UnaryPatternSyntax { RawKind: (int)SyntaxKind.NotPattern } notPattern)
        {
            return SyntaxFactory.IsPatternExpression(
                isPattern.Expression.WithoutTrivia(),
                notPattern.Pattern.WithoutTrivia());
        }

        return SyntaxFactory.IsPatternExpression(
            isPattern.Expression.WithoutTrivia(),
            SyntaxFactory.UnaryPattern(
                SyntaxFactory.Token(SyntaxKind.NotKeyword),
                isPattern.Pattern.WithoutTrivia()));
    }

    private static ExpressionSyntax Negate(ExpressionSyntax expression)
    {
        var operand = NeedsParenthesesForLogicalNot(expression)
            ? SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia())
            : expression.WithoutTrivia();

        return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, operand);
    }

    private static ExpressionSyntax ParenthesizeForLogicalOperand(ExpressionSyntax expression)
    {
        return expression is BinaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.LogicalAndExpression or (int)SyntaxKind.LogicalOrExpression
        }
            ? SyntaxFactory.ParenthesizedExpression(expression)
            : expression;
    }

    private static bool NeedsParenthesesForLogicalNot(ExpressionSyntax expression)
    {
        return expression.Kind() switch
        {
            SyntaxKind.IdentifierName or
            SyntaxKind.GenericName or
            SyntaxKind.PredefinedType or
            SyntaxKind.ThisExpression or
            SyntaxKind.BaseExpression or
            SyntaxKind.SimpleMemberAccessExpression or
            SyntaxKind.PointerMemberAccessExpression or
            SyntaxKind.ConditionalAccessExpression or
            SyntaxKind.InvocationExpression or
            SyntaxKind.ElementAccessExpression or
            SyntaxKind.ParenthesizedExpression or
            SyntaxKind.NumericLiteralExpression or
            SyntaxKind.StringLiteralExpression or
            SyntaxKind.CharacterLiteralExpression or
            SyntaxKind.TrueLiteralExpression or
            SyntaxKind.FalseLiteralExpression or
            SyntaxKind.NullLiteralExpression or
            SyntaxKind.DefaultLiteralExpression or
            SyntaxKind.DefaultExpression or
            SyntaxKind.TypeOfExpression or
            SyntaxKind.SizeOfExpression or
            SyntaxKind.SuppressNullableWarningExpression or
            SyntaxKind.LogicalNotExpression or
            SyntaxKind.UnaryMinusExpression or
            SyntaxKind.UnaryPlusExpression or
            SyntaxKind.BitwiseNotExpression or
            SyntaxKind.ObjectCreationExpression or
            SyntaxKind.ImplicitObjectCreationExpression or
            SyntaxKind.ArrayCreationExpression or
            SyntaxKind.ImplicitArrayCreationExpression or
            SyntaxKind.InterpolatedStringExpression or
            SyntaxKind.TupleExpression or
            SyntaxKind.PostIncrementExpression or
            SyntaxKind.PostDecrementExpression or
            SyntaxKind.AwaitExpression => false,
            _ => true
        };
    }

    private static bool IntroducesBoundVariable(ExpressionSyntax expression)
    {
        return expression.DescendantNodesAndSelf().Any(node =>
            node is DeclarationExpressionSyntax ||
            node is SingleVariableDesignationSyntax);
    }

    private static bool HasVariableDesignation(PatternSyntax pattern)
    {
        return pattern.DescendantNodesAndSelf().Any(node => node is SingleVariableDesignationSyntax);
    }

    private static string FormatCondition(ExpressionSyntax expression) =>
        expression.NormalizeWhitespace().ToFullString().Trim();

    private static string BuildDescription(string original, string inverted, bool createdElse)
    {
        var swap = createdElse
            ? "move the if body to else (empty if body)"
            : "swap if/else branches";
        return $"Invert if condition '{original}' to '{inverted}' and {swap}";
    }
}

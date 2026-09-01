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
        if (@params.AllFiles)
        {
            if (@params.Line.HasValue || @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with line or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!@params.Line.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "line is required.");

        if (@params.Line.Value < 1)
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
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var ifStatement = FindIfStatement(root, @params.Line!.Value, @params.Column);
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
                    File = @params.SourceFile!,
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

    /// <summary>
    /// Inverts every distinct eligible if in every C# document (same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>ConvertToPatternMatchingOperation.ExecuteAllFilesAsync</c>:
    /// <c>FilePath</c> ends with <c>.cs</c>). Incomplete or otherwise
    /// ineligible ifs and documents whose text is unchanged are skipped.
    /// When every file is a no-op, succeeds with empty changes.
    /// Nested ifs are inverted once each in a single rewrite pass
    /// (innermost first) so the same node is never double-inverted.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        InvertIfParams @params,
        CancellationToken cancellationToken)
    {
        var currentSolution = Context.Solution;
        var allDocuments = currentSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDocument = currentSolution.GetDocument(document.Id) ?? document;
            if (currentDocument is SourceGeneratedDocument)
                continue;

            var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                continue;

            var model = await currentDocument.GetSemanticModelAsync(cancellationToken);
            var newRoot = InvertAllIfs(root, model, out var invertedCount);
            if (invertedCount == 0)
                continue;

            var newDocument = currentDocument.WithSyntaxRoot(newRoot);
            var beforeText = await currentDocument.GetTextAsync(cancellationToken);
            var afterText = await newDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var span = root.GetLocation().GetLineSpan();
                allPendingChanges.Add(new PendingChange
                {
                    File = currentDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = BuildAllFilesDescription(invertedCount),
                    BeforeSnippet = root.NormalizeWhitespace().ToFullString().Trim(),
                    AfterSnippet = newRoot.NormalizeWhitespace().ToFullString().Trim(),
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                });
                continue;
            }

            currentSolution = newDocument.Project.Solution;
            anyChanged = true;
        }

        if (@params.Preview)
            return RefactoringResult.PreviewResult(operationId, allPendingChanges);

        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
            return RefactoringResult.Succeeded(operationId,
                new FileChanges
                {
                    FilesModified = commitResult.FilesModified,
                    FilesCreated = commitResult.FilesCreated,
                    FilesDeleted = commitResult.FilesDeleted
                },
                null, 0, 0);
        }

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
            null, 0, 0);
    }

    internal static string BuildAllFilesDescription(int invertedCount) =>
        invertedCount == 1
            ? "Invert if condition"
            : $"Invert {invertedCount} if conditions";

    /// <summary>
    /// Collects every distinct eligible if in <paramref name="root"/> using
    /// the existing invert helpers (not first-on-a-line). Incomplete and
    /// otherwise ineligible ifs are skipped. Nested ifs are distinct nodes.
    /// </summary>
    internal static IReadOnlyList<IfStatementSyntax> CollectInvertibleIfs(
        SyntaxNode root,
        SemanticModel? model) =>
        root.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(statement => TryInvert(statement, statement, model, out _))
            .ToList();

    /// <summary>
    /// Inverts every eligible if in a single rewrite pass. Children are
    /// visited first so nested ifs invert once, then the current node is
    /// inverted using the already-rewritten branches. The same node is
    /// never inverted twice.
    /// </summary>
    internal static SyntaxNode InvertAllIfs(
        SyntaxNode root,
        SemanticModel? model,
        out int invertedCount)
    {
        var rewriter = new InvertIfRewriter(model);
        var rewritten = rewriter.Visit(root);
        invertedCount = rewriter.InvertedCount;
        return rewritten ?? root;
    }

    /// <summary>
    /// Attempts an invert without throwing. Used by allFiles so incomplete
    /// or otherwise ineligible ifs stay no-ops. Eligibility is the same
    /// ConditionNotInvertible / incomplete-if paths as the single-site helpers.
    /// <paramref name="rewritten"/> supplies already-visited children so a
    /// nested invert is not discarded when the outer if is inverted.
    /// </summary>
    internal static bool TryInvert(
        IfStatementSyntax original,
        IfStatementSyntax rewritten,
        SemanticModel? model,
        out IfStatementSyntax inverted)
    {
        inverted = rewritten;

        if (original.Condition.IsMissing || original.Statement.IsMissing)
            return false;

        try
        {
            var invertedCondition = InvertCondition(original.Condition, model);
            inverted = RewriteIf(rewritten, invertedCondition);
            return true;
        }
        catch (RefactoringException)
        {
            inverted = rewritten;
            return false;
        }
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
            // An unbraced if-as-then would capture the new outer else
            // (dangling else): `if (!a) if (b) B(); else A();`.
            newThen = originalElse is IfStatementSyntax
                ? SyntaxFactory.Block(originalElse)
                : originalElse;
            newElse = ifStatement.Else!.WithStatement(originalThen);
        }

        return ifStatement
            .WithCondition(invertedCondition.WithTriviaFrom(ifStatement.Condition))
            .WithStatement(newThen)
            .WithElse(newElse);
    }

    private static ExpressionSyntax InvertExpression(ExpressionSyntax expression, SemanticModel? model)
    {
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
                    SyntaxFactory.Token(SyntaxKind.BarBarToken).WithTriviaFrom(binary.OperatorToken),
                    ParenthesizeForLogicalOperand(InvertExpression(binary.Right, model)));

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression):
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalAndExpression,
                    ParenthesizeForLogicalOperand(InvertExpression(binary.Left, model)),
                    SyntaxFactory.Token(SyntaxKind.AmpersandAmpersandToken).WithTriviaFrom(binary.OperatorToken),
                    ParenthesizeForLogicalOperand(InvertExpression(binary.Right, model)));

            case BinaryExpressionSyntax binary when TryFlipComparison(binary, model, out var flipped):
                return flipped;

            case IsPatternExpressionSyntax isPattern:
                return InvertIsPattern(isPattern);
        }

        return Negate(expression, model);
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

        if (model != null && !IsSafeToFlipComparison(binary, model, newKind.Value))
            return false;

        var operatorToken = SyntaxFactory.Token(OperatorTokenKind(newKind.Value))
            .WithTriviaFrom(binary.OperatorToken);
        flipped = SyntaxFactory.BinaryExpression(newKind.Value, binary.Left, operatorToken, binary.Right);
        return true;
    }

    private static SyntaxKind OperatorTokenKind(SyntaxKind expressionKind) => expressionKind switch
    {
        SyntaxKind.GreaterThanExpression => SyntaxKind.GreaterThanToken,
        SyntaxKind.LessThanExpression => SyntaxKind.LessThanToken,
        SyntaxKind.GreaterThanOrEqualExpression => SyntaxKind.GreaterThanEqualsToken,
        SyntaxKind.LessThanOrEqualExpression => SyntaxKind.LessThanEqualsToken,
        SyntaxKind.EqualsExpression => SyntaxKind.EqualsEqualsToken,
        SyntaxKind.NotEqualsExpression => SyntaxKind.ExclamationEqualsToken,
        _ => throw new ArgumentOutOfRangeException(nameof(expressionKind), expressionKind, null)
    };

    private static bool IsSafeToFlipComparison(
        BinaryExpressionSyntax binary,
        SemanticModel model,
        SyntaxKind flippedKind)
    {
        if (model.GetSymbolInfo(binary).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            return false;

        var leftType = model.GetTypeInfo(binary.Left).Type;
        var rightType = model.GetTypeInfo(binary.Right).Type;
        if (IsFloatingPoint(leftType) || IsFloatingPoint(rightType))
            return false;

        // Lifted nullable relational ops: null > 0 and null <= 0 are both false,
        // so flipping the operator is not negation.
        if (IsRelationalComparison(flippedKind) &&
            (IsNullableValueType(leftType) || IsNullableValueType(rightType)))
            return false;

        return true;
    }

    private static bool IsRelationalComparison(SyntaxKind kind) => kind is
        SyntaxKind.GreaterThanExpression or
        SyntaxKind.LessThanExpression or
        SyntaxKind.GreaterThanOrEqualExpression or
        SyntaxKind.LessThanOrEqualExpression;

    private static bool IsFloatingPoint(ITypeSymbol? type)
    {
        var underlying = UnwrapNullable(type);
        return underlying?.SpecialType is SpecialType.System_Single or SpecialType.System_Double;
    }

    private static bool IsNullableValueType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named &&
        named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named &&
        named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? named.TypeArguments[0]
            : type;

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

    private static ExpressionSyntax Negate(ExpressionSyntax expression, SemanticModel? model)
    {
        if (model != null && !CanApplyLogicalNot(expression, model))
        {
            throw new RefactoringException(
                ErrorCodes.ConditionNotInvertible,
                "Condition cannot be safely inverted because logical negation is not defined for this type.");
        }

        var operand = NeedsParenthesesForLogicalNot(expression)
            ? SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia())
            : expression.WithoutTrivia();

        return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, operand)
            .WithLeadingTrivia(expression.GetLeadingTrivia())
            .WithTrailingTrivia(expression.GetTrailingTrivia());
    }

    private static bool CanApplyLogicalNot(ExpressionSyntax expression, SemanticModel model)
    {
        var info = model.GetTypeInfo(expression);
        if (IsBooleanLike(info.Type) || IsBooleanLike(info.ConvertedType))
            return true;

        var type = info.Type;
        if (type == null || type.TypeKind == TypeKind.Error)
            return false;

        return HasLogicalNotOperator(type);
    }

    private static bool IsBooleanLike(ITypeSymbol? type)
    {
        if (type == null)
            return false;

        if (type.SpecialType == SpecialType.System_Boolean)
            return true;

        return type is INamedTypeSymbol named &&
               named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
               named.TypeArguments[0].SpecialType == SpecialType.System_Boolean;
    }

    private static bool HasLogicalNotOperator(ITypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetMembers("op_LogicalNot").OfType<IMethodSymbol>().Any(method =>
                    method.MethodKind == MethodKind.UserDefinedOperator &&
                    method.Parameters.Length == 1))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Visits innermost ifs first, then inverts the current node so nested
    /// and outer ifs each invert once without replacing an ancestor and
    /// descendant independently.
    /// </summary>
    private sealed class InvertIfRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel? _model;

        public InvertIfRewriter(SemanticModel? model)
        {
            _model = model;
        }

        public int InvertedCount { get; private set; }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var rewritten = (IfStatementSyntax)base.VisitIfStatement(node)!;
            if (!TryInvert(node, rewritten, _model, out var inverted))
                return rewritten;

            InvertedCount++;
            return inverted;
        }
    }
}

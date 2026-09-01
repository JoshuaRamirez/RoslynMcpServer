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
/// Converts if/is chains and switch statements to switch expressions with pattern matching.
/// </summary>
public sealed class ConvertToPatternMatchingOperation : RefactoringOperationBase<ConvertToPatternMatchingParams>
{
    /// <inheritdoc />
    public ConvertToPatternMatchingOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToPatternMatchingParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-to-pattern-matching parameters. Internal so tests
    /// can exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertToPatternMatchingParams @params)
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

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToPatternMatchingParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var target = FindConvertibleStatement(root, @params.Line!.Value, @params.Column);

        if (target is SwitchStatementSyntax switchStmt)
        {
            return await ConvertSwitchToExpression(operationId, document, root, switchStmt, @params, cancellationToken);
        }

        if (target is IfStatementSyntax ifStmt)
        {
            return await ConvertIfChainToSwitch(operationId, document, root, ifStmt, @params, cancellationToken);
        }

        var location = @params.Column.HasValue
            ? $"line {@params.Line}, column {@params.Column.Value}"
            : $"line {@params.Line}";
        throw new RefactoringException(ErrorCodes.CannotConvert,
            $"No switch statement or if/is chain found at {location}.");
    }

    /// <summary>
    /// Converts every distinct eligible switch or if-chain in every C# document
    /// (same document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>ConvertForeachLinqOperation.ExecuteAllFilesAsync</c>:
    /// <c>FilePath</c> ends with <c>.cs</c>). Statements that cannot convert
    /// and documents whose text is unchanged are skipped. When every file is
    /// a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ConvertToPatternMatchingParams @params,
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

            var replacements = CollectConvertibleReplacements(root);

            if (replacements.Count == 0)
                continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
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
                    Description = BuildAllFilesDescription(replacements.Count),
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

    internal static string BuildAllFilesDescription(int convertedCount) =>
        convertedCount == 1
            ? "Convert switch/if to switch expression"
            : $"Convert {convertedCount} switch/if statements to switch expressions";

    /// <summary>
    /// Collects every distinct eligible switch or if-chain in
    /// <paramref name="root"/> using the existing conversion helpers (not
    /// first-on-a-line). Nested convertible statements keep the outermost so
    /// an if/else-if chain is one rewrite and <c>ReplaceNodes</c> does not
    /// rewrite an ancestor and descendant together.
    /// </summary>
    internal static IReadOnlyList<StatementSyntax> CollectConvertibleStatements(SyntaxNode root) =>
        CollectConvertibleReplacements(root).Keys.ToList();

    /// <summary>
    /// Builds replacements for every distinct eligible switch or if-chain.
    /// Used by allFiles so <see cref="TryConvert"/> runs once per statement.
    /// </summary>
    internal static IReadOnlyDictionary<StatementSyntax, SyntaxNode> CollectConvertibleReplacements(SyntaxNode root)
    {
        var replacements = new Dictionary<StatementSyntax, SyntaxNode>();
        foreach (var statement in root.DescendantNodes().OfType<SwitchStatementSyntax>().Cast<StatementSyntax>()
                     .Concat(root.DescendantNodes().OfType<IfStatementSyntax>()))
        {
            if (TryConvert(statement, out var replacement))
                replacements[statement] = replacement;
        }

        return replacements
            .Where(pair => !replacements.Keys.Any(other => other != pair.Key && other.Contains(pair.Key)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>
    /// Attempts a conversion without throwing. Used by allFiles so switch/if
    /// statements that cannot convert stay no-ops. Eligibility is the same
    /// CannotConvert paths as the single-site helpers.
    /// </summary>
    internal static bool TryConvert(StatementSyntax statement, out StatementSyntax replacement)
    {
        try
        {
            if (statement is SwitchStatementSyntax switchStmt)
            {
                replacement = WrapAsReturn(switchStmt, BuildSwitchExpression(switchStmt));
                return true;
            }

            if (statement is IfStatementSyntax ifStmt)
            {
                replacement = WrapAsReturn(ifStmt, BuildIfChainSwitch(ifStmt));
                return true;
            }

            replacement = statement;
            return false;
        }
        catch (RefactoringException)
        {
            replacement = statement;
            return false;
        }
    }

    /// <summary>
    /// Finds a convertible switch or if. When <paramref name="column"/> is
    /// omitted, keeps today's first-match whose start line equals
    /// <paramref name="line"/> (switch before if). When set, picks the
    /// smallest switch or if whose span covers that 1-based column. Do not
    /// require the statement to start on <paramref name="line"/> when
    /// column is set — a split <c>switch</c>/<c>if</c> may put the keyword
    /// on a continuation line. Do not fall back to the start-line pick
    /// when no covering node exists.
    /// </summary>
    internal static StatementSyntax? FindConvertibleStatement(SyntaxNode root, int line, int? column)
    {
        var switches = root.DescendantNodes().OfType<SwitchStatementSyntax>();
        var ifs = root.DescendantNodes().OfType<IfStatementSyntax>();

        if (!column.HasValue)
        {
            StatementSyntax? firstSwitch = switches.FirstOrDefault(statement => StartsOnLine(statement, line));
            if (firstSwitch != null)
                return firstSwitch;

            return ifs.FirstOrDefault(statement => StartsOnLine(statement, line));
        }

        return switches.Cast<StatementSyntax>()
            .Concat(ifs)
            .Where(statement => SpanCoversColumn(statement.GetLocation().GetLineSpan(), line, column.Value))
            .OrderBy(statement => statement.Span.Length)
            .FirstOrDefault();
    }

    private static bool StartsOnLine(SyntaxNode node, int line) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line;

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent statement also
    /// match the previous switch or if.
    /// </summary>
    internal static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column >= endCol)
            return false;
        return true;
    }

    private async Task<RefactoringResult> ConvertSwitchToExpression(
        Guid operationId, Document document, SyntaxNode root,
        SwitchStatementSyntax switchStmt, ConvertToPatternMatchingParams @params,
        CancellationToken cancellationToken)
    {
        var switchExpr = BuildSwitchExpression(switchStmt);

        var before = switchStmt.NormalizeWhitespace().ToFullString();
        var after = switchExpr.NormalizeWhitespace().ToFullString();

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile!,
                    ChangeType = ChangeKind.Modify,
                    Description = "Convert switch statement to switch expression",
                    BeforeSnippet = before.Length > 200 ? before[..200] + "..." : before,
                    AfterSnippet = after.Length > 200 ? after[..200] + "..." : after
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newRoot = root.ReplaceNode(switchStmt, WrapAsReturn(switchStmt, switchExpr));
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            null, 0, 0);
    }

    private async Task<RefactoringResult> ConvertIfChainToSwitch(
        Guid operationId, Document document, SyntaxNode root,
        IfStatementSyntax ifStmt, ConvertToPatternMatchingParams @params,
        CancellationToken cancellationToken)
    {
        var switchExpr = BuildIfChainSwitch(ifStmt);

        var before = ifStmt.NormalizeWhitespace().ToFullString();
        var after = switchExpr.NormalizeWhitespace().ToFullString();

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile!,
                    ChangeType = ChangeKind.Modify,
                    Description = "Convert if/is chain to switch expression",
                    BeforeSnippet = before.Length > 200 ? before[..200] + "..." : before,
                    AfterSnippet = after.Length > 200 ? after[..200] + "..." : after
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newRoot = root.ReplaceNode(ifStmt, WrapAsReturn(ifStmt, switchExpr));
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            null, 0, 0);
    }

    /// <summary>
    /// Builds a switch expression from a switch statement. Throws the same
    /// CannotConvert errors as today's single-site path.
    /// </summary>
    private static SwitchExpressionSyntax BuildSwitchExpression(SwitchStatementSyntax switchStmt)
    {
        var arms = new List<SwitchExpressionArmSyntax>();

        foreach (var section in switchStmt.Sections)
        {
            // Get the expression from the section body (return or assignment)
            var bodyExpr = ExtractSectionExpression(section);
            if (bodyExpr == null)
                throw new RefactoringException(ErrorCodes.CannotConvert,
                    "Switch section body must be a simple return or assignment statement.");

            foreach (var label in section.Labels)
            {
                PatternSyntax pattern;
                WhenClauseSyntax? whenClause = null;

                switch (label)
                {
                    case CaseSwitchLabelSyntax caseLabel:
                        pattern = SyntaxFactory.ConstantPattern(caseLabel.Value);
                        break;
                    case CasePatternSwitchLabelSyntax patternLabel:
                        pattern = patternLabel.Pattern;
                        whenClause = patternLabel.WhenClause;
                        break;
                    case DefaultSwitchLabelSyntax:
                        pattern = SyntaxFactory.DiscardPattern();
                        break;
                    default:
                        throw new RefactoringException(ErrorCodes.CannotConvert,
                            "Unsupported switch label type.");
                }

                var arm = SyntaxFactory.SwitchExpressionArm(pattern, bodyExpr);
                if (whenClause != null)
                    arm = arm.WithWhenClause(whenClause);

                arms.Add(arm);
            }
        }

        if (arms.Count == 0)
            throw new RefactoringException(ErrorCodes.CannotConvert, "No convertible switch sections found.");

        return SyntaxFactory.SwitchExpression(
            switchStmt.Expression,
            SyntaxFactory.SeparatedList(arms));
    }

    /// <summary>
    /// Builds a switch expression from an if/is chain. Throws the same
    /// CannotConvert errors as today's single-site path.
    /// </summary>
    private static SwitchExpressionSyntax BuildIfChainSwitch(IfStatementSyntax ifStmt)
    {
        var arms = new List<SwitchExpressionArmSyntax>();
        var current = ifStmt;

        // Try to find the common expression being tested (e.g., 'x is Type')
        ExpressionSyntax? governingExpr = null;

        while (current != null)
        {
            if (current.Condition is IsPatternExpressionSyntax isPattern)
            {
                if (governingExpr == null)
                    governingExpr = isPattern.Expression;

                var bodyExpr = ExtractIfBodyExpression(current.Statement);
                if (bodyExpr == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert,
                        "If body must be a simple return or assignment statement.");

                var arm = SyntaxFactory.SwitchExpressionArm(isPattern.Pattern, bodyExpr);
                arms.Add(arm);
            }
            else if (current.Condition is BinaryExpressionSyntax binary &&
                     binary.IsKind(SyntaxKind.EqualsExpression))
            {
                if (governingExpr == null)
                    governingExpr = binary.Left;

                var bodyExpr = ExtractIfBodyExpression(current.Statement);
                if (bodyExpr == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert,
                        "If body must be a simple return or assignment statement.");

                var arm = SyntaxFactory.SwitchExpressionArm(
                    SyntaxFactory.ConstantPattern(binary.Right), bodyExpr);
                arms.Add(arm);
            }
            else
            {
                throw new RefactoringException(ErrorCodes.CannotConvert,
                    "If condition must be an 'is' pattern or equality check.");
            }

            // Move to else-if or else
            if (current.Else?.Statement is IfStatementSyntax nextIf)
            {
                current = nextIf;
            }
            else
            {
                // Handle else clause (default arm)
                if (current.Else != null)
                {
                    var elseExpr = ExtractIfBodyExpression(current.Else.Statement);
                    if (elseExpr != null)
                    {
                        arms.Add(SyntaxFactory.SwitchExpressionArm(
                            SyntaxFactory.DiscardPattern(), elseExpr));
                    }
                }
                current = null;
            }
        }

        if (arms.Count < 2 || governingExpr == null)
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "Need at least 2 branches in the if/is chain to convert.");

        return SyntaxFactory.SwitchExpression(
            governingExpr,
            SyntaxFactory.SeparatedList(arms));
    }

    private static ReturnStatementSyntax WrapAsReturn(StatementSyntax original, SwitchExpressionSyntax switchExpr) =>
        SyntaxFactory.ReturnStatement(switchExpr)
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());

    private static ExpressionSyntax? ExtractSectionExpression(SwitchSectionSyntax section)
    {
        foreach (var stmt in section.Statements)
        {
            switch (stmt)
            {
                case ReturnStatementSyntax returnStmt:
                    return returnStmt.Expression;
                case ExpressionStatementSyntax exprStmt when exprStmt.Expression is AssignmentExpressionSyntax assign:
                    return assign.Right;
                case BreakStatementSyntax:
                    continue;
                case BlockSyntax block:
                    foreach (var inner in block.Statements)
                    {
                        if (inner is ReturnStatementSyntax ret) return ret.Expression;
                    }
                    break;
            }
        }
        return null;
    }

    private static ExpressionSyntax? ExtractIfBodyExpression(StatementSyntax body)
    {
        if (body is BlockSyntax block)
        {
            var stmts = block.Statements.Where(s => s is not BreakStatementSyntax).ToList();
            if (stmts.Count == 1) body = stmts[0];
            else return null;
        }

        return body switch
        {
            ReturnStatementSyntax returnStmt => returnStmt.Expression,
            ExpressionStatementSyntax exprStmt when exprStmt.Expression is AssignmentExpressionSyntax assign => assign.Right,
            _ => null
        };
    }
}

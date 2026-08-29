using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Removes braces from control statements that have a single-statement block
/// body (UC-A6 remove_braces): if, else, for, foreach, while, using.
/// </summary>
public sealed class RemoveBracesOperation : RefactoringOperationBase<RemoveBracesParams>
{
    internal const string ScopeStatement = "statement";
    internal const string ScopeFile = "file";
    internal const string ScopeType = "type";

    /// <summary>
    /// Creates a new remove-braces operation.
    /// </summary>
    public RemoveBracesOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RemoveBracesParams @params) => Validate(@params);

    /// <summary>
    /// Validates remove-braces parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(RemoveBracesParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        var scope = NormalizeScope(@params.Scope);

        if (scope == ScopeStatement)
        {
            if (!@params.Line.HasValue)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line is required when scope is statement.");

            if (@params.Line.Value < 1)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        }
        else if (@params.Line.HasValue && @params.Line.Value < 1)
        {
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        }

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (scope == ScopeType && string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required when scope is type.");

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
        RemoveBracesParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var scope = NormalizeScope(@params.Scope);
        HashSet<StatementSyntax>? onlyThese = null;
        TypeDeclarationSyntax? typeScope = null;
        SyntaxNode? previewOwner = null;

        if (scope == ScopeStatement)
        {
            var target = FindControlTarget(root, @params.Line!.Value, @params.Column);
            if (target == null)
            {
                throw new RefactoringException(
                    ErrorCodes.NoControlStatement,
                    $"No control statement found at line {@params.Line.Value}" +
                    (@params.Column.HasValue ? $", column {@params.Column.Value}" : "") +
                    ".");
            }

            if (target.Value.Body is not BlockSyntax block)
            {
                throw new RefactoringException(
                    ErrorCodes.NoBracesToRemove,
                    "Statement does not have braces.");
            }

            if (block.Statements.Count != 1)
            {
                throw new RefactoringException(
                    ErrorCodes.MultipleStatementsInBlock,
                    block.Statements.Count == 0
                        ? "Block must contain exactly one statement."
                        : "Block contains multiple statements.");
            }

            if (WouldCreateDanglingElse(target.Value.Owner, block.Statements[0]))
            {
                throw new RefactoringException(
                    ErrorCodes.CompilationError,
                    "Removing these braces would change how a following else binds (dangling else).");
            }

            if (WouldHideExternallyReferencedLabel(block.Statements[0]))
            {
                throw new RefactoringException(
                    ErrorCodes.CompilationError,
                    "Removing these braces would change the scope of a label referenced by an external goto.");
            }

            onlyThese = [target.Value.Body];
            previewOwner = target.Value.Owner;
        }
        else if (scope == ScopeType)
        {
            typeScope = FindTypeDeclaration(root, @params.TypeName!);
        }

        var rewriter = new BraceRewriter(onlyThese, typeScope, unwrapElseIf: onlyThese != null, previewOwner);
        var newRoot = rewriter.Visit(root) ?? root;
        if (rewriter.UnwrappedCount > 0)
        {
            newRoot = Formatter.Format(
                newRoot,
                BraceRewriter.FormatAnnotation,
                Context.Workspace,
                cancellationToken: cancellationToken);
        }

        if (scope != ScopeStatement && rewriter.UnwrappedCount == 0)
        {
            // File/type with nothing to unwrap is a successful no-op (same spirit as
            // already-sorted sort_usings). Statement scope already rejected above.
        }

        var description = BuildDescription(scope, rewriter.UnwrappedCount, @params.TypeName);
        var beforeSnippet = previewOwner?.NormalizeWhitespace().ToFullString().Trim();
        SyntaxNode? afterOwner = null;
        if (previewOwner != null && rewriter.UnwrappedCount > 0)
        {
            afterOwner = newRoot.GetAnnotatedNodes(BraceRewriter.OwnerAnnotation).FirstOrDefault();
        }

        var afterSnippet = afterOwner?.NormalizeWhitespace().ToFullString().Trim()
            ?? (rewriter.UnwrappedCount > 0
                ? newRoot.NormalizeWhitespace().ToFullString().Trim()
                : beforeSnippet);

        if (@params.Preview)
        {
            var span = (previewOwner ?? root).GetLocation().GetLineSpan();
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
                StatementsModified = rewriter.UnwrappedCount,
                Scope = scope
            };
        }

        if (rewriter.UnwrappedCount == 0)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Changes = new FileChanges
                {
                    FilesModified = [],
                    FilesCreated = [],
                    FilesDeleted = []
                },
                StatementsModified = 0,
                Scope = scope
            };
        }

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
            StatementsModified = rewriter.UnwrappedCount,
            Scope = scope
        };
    }

    internal static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return ScopeStatement;

        var normalized = scope.Trim().ToLowerInvariant();
        if (normalized is ScopeStatement or ScopeFile or ScopeType)
            return normalized;

        throw new RefactoringException(
            ErrorCodes.MissingRequiredParam,
            "scope must be statement, file, or type.");
    }

    internal static TypeDeclarationSyntax FindTypeDeclaration(SyntaxNode root, string typeName)
    {
        var matches = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(type => TypeNameMatches(type, typeName))
            .ToList();

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found.");
        }

        if (matches.Count > 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple types named '{typeName}' found. Provide a namespace-qualified typeName to disambiguate.");
        }

        return matches[0];
    }

    internal static bool TypeNameMatches(TypeDeclarationSyntax type, string typeName)
    {
        var qualified = GetQualifiedTypeName(type);
        if (qualified.Equals(typeName, StringComparison.Ordinal))
            return true;

        if (type.Identifier.Text.Equals(typeName, StringComparison.Ordinal))
            return true;

        return qualified.EndsWith("." + typeName, StringComparison.Ordinal);
    }

    internal static string GetQualifiedTypeName(TypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        for (var current = (SyntaxNode)type; current != null; current = current.Parent)
        {
            switch (current)
            {
                case TypeDeclarationSyntax declared:
                    parts.Insert(0, declared.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.InsertRange(0, ns.Name.ToString().Split('.'));
                    break;
            }
        }

        return string.Join(".", parts);
    }

    /// <summary>
    /// True when unwrapping a block whose single statement is
    /// <paramref name="inner"/> would let a following <c>else</c> bind to a
    /// nested <c>if</c> (dangling else) or produce a second <c>else</c>.
    /// </summary>
    internal static bool WouldCreateDanglingElse(SyntaxNode owner, StatementSyntax inner)
    {
        if (owner is not IfStatementSyntax ifStatement || ifStatement.Else == null)
            return false;

        return EmbeddedStatementWouldCaptureFollowingElse(inner);
    }

    /// <summary>
    /// True when wrapping <paramref name="body"/> in a new block would hide a
    /// label that a <c>goto</c> outside that body currently resolves. Used in
    /// reverse: do not unwrap a block if the inner statement is that labeled
    /// body. Labels already nested in an inner block stay hidden either way.
    /// </summary>
    internal static bool WouldHideExternallyReferencedLabel(StatementSyntax body)
    {
        var container = GetLabelContainer(body);
        if (container == null)
            return false;

        foreach (var label in body.DescendantNodesAndSelf().OfType<LabeledStatementSyntax>())
        {
            if (IsLabelAlreadyNestedInInnerBlock(label, body))
                continue;

            var name = label.Identifier.ValueText;
            foreach (var gotoStatement in container.DescendantNodes().OfType<GotoStatementSyntax>())
            {
                if (!gotoStatement.IsKind(SyntaxKind.GotoStatement))
                    continue;

                if (!string.Equals(GetGotoLabelName(gotoStatement), name, StringComparison.Ordinal))
                    continue;

                if (GetLabelContainer(gotoStatement) != container)
                    continue;

                if (body.Contains(gotoStatement))
                    continue;

                return true;
            }
        }

        return false;
    }

    private static bool EmbeddedStatementWouldCaptureFollowingElse(StatementSyntax statement)
    {
        while (true)
        {
            switch (statement)
            {
                case IfStatementSyntax:
                    return true;
                case LabeledStatementSyntax labeled:
                    statement = labeled.Statement;
                    continue;
                case ForStatementSyntax forStatement:
                    statement = forStatement.Statement;
                    continue;
                case CommonForEachStatementSyntax foreachStatement:
                    statement = foreachStatement.Statement;
                    continue;
                case WhileStatementSyntax whileStatement:
                    statement = whileStatement.Statement;
                    continue;
                case UsingStatementSyntax usingStatement:
                    statement = usingStatement.Statement;
                    continue;
                case LockStatementSyntax lockStatement:
                    statement = lockStatement.Statement;
                    continue;
                case FixedStatementSyntax fixedStatement:
                    statement = fixedStatement.Statement;
                    continue;
                case BlockSyntax block when block.Statements.Count == 1:
                    statement = block.Statements[0];
                    continue;
                default:
                    return false;
            }
        }
    }

    private static bool IsLabelAlreadyNestedInInnerBlock(LabeledStatementSyntax label, StatementSyntax body)
    {
        if (label == body)
            return false;

        foreach (var ancestor in label.Ancestors())
        {
            if (ancestor == body)
                return false;

            if (ancestor is BlockSyntax or SwitchSectionSyntax)
                return true;
        }

        return false;
    }

    private static string? GetGotoLabelName(GotoStatementSyntax gotoStatement) =>
        gotoStatement.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            { } expression => expression.ToString(),
            _ => null
        };

    private static SyntaxNode? GetLabelContainer(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(ancestor => ancestor is
            MethodDeclarationSyntax or
            LocalFunctionStatementSyntax or
            AnonymousFunctionExpressionSyntax or
            AccessorDeclarationSyntax or
            ConstructorDeclarationSyntax or
            DestructorDeclarationSyntax or
            OperatorDeclarationSyntax or
            ConversionOperatorDeclarationSyntax);

    internal static ControlTarget? FindControlTarget(SyntaxNode root, int line, int? column)
    {
        var onLine = CollectTargets(root)
            .Where(target => KeywordIsOnLine(target.Keyword, line))
            .ToList();

        if (onLine.Count == 0)
            return null;

        if (column.HasValue)
        {
            var atColumn = onLine
                .Where(target => KeywordCoversColumn(target.Keyword, line, column.Value))
                .OrderBy(target => target.Keyword.Span.Length)
                .ToList();
            return atColumn.Count == 0 ? null : atColumn[0];
        }

        return onLine.OrderBy(target => target.Keyword.SpanStart).First();
    }

    internal static IEnumerable<ControlTarget> CollectTargets(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax ifStatement:
                    yield return new ControlTarget(ifStatement, ifStatement.Statement, ifStatement.IfKeyword);
                    if (ifStatement.Else != null)
                    {
                        yield return new ControlTarget(
                            ifStatement,
                            ifStatement.Else.Statement,
                            ifStatement.Else.ElseKeyword);
                    }

                    break;

                case ForStatementSyntax forStatement:
                    yield return new ControlTarget(forStatement, forStatement.Statement, forStatement.ForKeyword);
                    break;

                case CommonForEachStatementSyntax forEachStatement:
                    yield return new ControlTarget(forEachStatement, forEachStatement.Statement, forEachStatement.ForEachKeyword);
                    break;

                case WhileStatementSyntax whileStatement:
                    yield return new ControlTarget(whileStatement, whileStatement.Statement, whileStatement.WhileKeyword);
                    break;

                case UsingStatementSyntax usingStatement:
                    yield return new ControlTarget(usingStatement, usingStatement.Statement, usingStatement.UsingKeyword);
                    break;
            }
        }
    }

    internal static StatementSyntax UnwrapBlock(BlockSyntax block)
    {
        if (block.Statements.Count != 1)
            return block;

        return block.Statements[0];
    }

    private static bool KeywordIsOnLine(SyntaxToken keyword, int line)
    {
        var span = keyword.GetLocation().GetLineSpan();
        return span.StartLinePosition.Line + 1 == line;
    }

    private static bool KeywordCoversColumn(SyntaxToken keyword, int line, int column)
    {
        return SpanCoversColumn(keyword.GetLocation().GetLineSpan(), line, column);
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

    private static string BuildDescription(string scope, int count, string? typeName)
    {
        var noun = count == 1 ? "control statement" : "control statements";
        return scope switch
        {
            ScopeType => $"Remove braces from {count} {noun} in type '{typeName}'",
            ScopeFile => $"Remove braces from {count} {noun} in file",
            _ => $"Remove braces from {count} {noun}"
        };
    }

    /// <summary>
    /// A control statement (or else clause) that can lose braces.
    /// </summary>
    /// <param name="Owner">The if/for/foreach/while/using statement that owns the body.</param>
    /// <param name="Body">The embedded statement that may be a single-statement block.</param>
    /// <param name="Keyword">The keyword the user points at (if, else, for, foreach, while, using).</param>
    internal readonly record struct ControlTarget(SyntaxNode Owner, StatementSyntax Body, SyntaxToken Keyword);

    private sealed class BraceRewriter : CSharpSyntaxRewriter
    {
        internal static readonly SyntaxAnnotation OwnerAnnotation = new("remove-braces-owner");
        internal static readonly SyntaxAnnotation FormatAnnotation = new("remove-braces-format");

        private readonly HashSet<StatementSyntax>? _onlyThese;
        private readonly TypeDeclarationSyntax? _typeScope;
        private readonly bool _unwrapElseIf;
        private readonly SyntaxNode? _previewOwner;

        public int UnwrappedCount { get; private set; }

        public BraceRewriter(
            HashSet<StatementSyntax>? onlyThese,
            TypeDeclarationSyntax? typeScope,
            bool unwrapElseIf,
            SyntaxNode? previewOwner)
        {
            _onlyThese = onlyThese;
            _typeScope = typeScope;
            _unwrapElseIf = unwrapElseIf;
            _previewOwner = previewOwner;
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var rewritten = (IfStatementSyntax)base.VisitIfStatement(node)!;
            if (ShouldUnwrap(node.Statement, node))
            {
                rewritten = rewritten.WithStatement(UnwrapRewrittenBody(rewritten.Statement))
                    .WithAdditionalAnnotations(FormatAnnotation);
                UnwrappedCount++;
            }

            return AnnotateIfPreviewOwner(node, rewritten);
        }

        public override SyntaxNode? VisitElseClause(ElseClauseSyntax node)
        {
            var rewritten = (ElseClauseSyntax)base.VisitElseClause(node)!;

            // Roslyn IDE0011 treats else-if as one construct: unwrap the inner
            // if body, not the else around the if. Statement scope can still
            // target the else keyword explicitly and unwrap that else-if block.
            if (IsElseIfConstruct(node) && !_unwrapElseIf)
                return rewritten;

            if (ShouldUnwrap(node.Statement, node))
            {
                rewritten = rewritten.WithStatement(UnwrapRewrittenBody(rewritten.Statement))
                    .WithAdditionalAnnotations(FormatAnnotation);
                UnwrappedCount++;
            }

            return rewritten;
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var rewritten = (ForStatementSyntax)base.VisitForStatement(node)!;
            if (ShouldUnwrap(node.Statement, node))
            {
                rewritten = rewritten.WithStatement(UnwrapRewrittenBody(rewritten.Statement))
                    .WithAdditionalAnnotations(FormatAnnotation);
                UnwrappedCount++;
            }

            return AnnotateIfPreviewOwner(node, rewritten);
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var rewritten = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            if (ShouldUnwrap(node.Statement, node))
            {
                rewritten = rewritten.WithStatement(UnwrapRewrittenBody(rewritten.Statement))
                    .WithAdditionalAnnotations(FormatAnnotation);
                UnwrappedCount++;
            }

            return AnnotateIfPreviewOwner(node, rewritten);
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var rewritten = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            if (ShouldUnwrap(node.Statement, node))
            {
                rewritten = rewritten.WithStatement(UnwrapRewrittenBody(rewritten.Statement))
                    .WithAdditionalAnnotations(FormatAnnotation);
                UnwrappedCount++;
            }

            return AnnotateIfPreviewOwner(node, rewritten);
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var rewritten = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            if (ShouldUnwrap(node.Statement, node))
            {
                rewritten = rewritten.WithStatement(UnwrapRewrittenBody(rewritten.Statement))
                    .WithAdditionalAnnotations(FormatAnnotation);
                UnwrappedCount++;
            }

            return AnnotateIfPreviewOwner(node, rewritten);
        }

        public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
        {
            var rewritten = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
            if (ShouldUnwrap(node.Statement, node))
            {
                rewritten = rewritten.WithStatement(UnwrapRewrittenBody(rewritten.Statement))
                    .WithAdditionalAnnotations(FormatAnnotation);
                UnwrappedCount++;
            }

            return AnnotateIfPreviewOwner(node, rewritten);
        }

        private static bool IsElseIfConstruct(ElseClauseSyntax node)
        {
            return node.Statement is IfStatementSyntax
                || (node.Statement is BlockSyntax block
                    && block.Statements.Count == 1
                    && block.Statements[0] is IfStatementSyntax);
        }

        private bool ShouldUnwrap(StatementSyntax originalBody, SyntaxNode owner)
        {
            if (originalBody is not BlockSyntax block || originalBody.IsMissing)
                return false;

            if (block.Statements.Count != 1)
                return false;

            var inner = block.Statements[0];

            if (!_unwrapElseIf && owner is ElseClauseSyntax && inner is IfStatementSyntax)
                return false;

            if (WouldCreateDanglingElse(owner, inner))
                return false;

            if (WouldHideExternallyReferencedLabel(inner))
                return false;

            if (_onlyThese != null)
                return _onlyThese.Contains(originalBody);

            if (_typeScope != null && !originalBody.Ancestors().Contains(_typeScope))
                return false;

            return true;
        }

        private static StatementSyntax UnwrapRewrittenBody(StatementSyntax rewrittenBody)
        {
            return rewrittenBody is BlockSyntax rewrittenBlock
                ? UnwrapBlock(rewrittenBlock)
                : rewrittenBody;
        }

        private SyntaxNode AnnotateIfPreviewOwner(SyntaxNode original, SyntaxNode rewritten)
        {
            return original == _previewOwner
                ? rewritten.WithAdditionalAnnotations(OwnerAnnotation)
                : rewritten;
        }
    }
}

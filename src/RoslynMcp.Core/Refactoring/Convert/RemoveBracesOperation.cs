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
using RoslynMcp.Core.Refactoring.Utilities;
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
        var scope = BraceTypeNameHelpers.NormalizeScope(@params.Scope);

        // Mirror AddBracesOperation: AllFiles cannot be combined with a
        // location/name scope. statement and type stay single-file only.
        // Omitted scope (null/whitespace) is not an explicit statement pick —
        // AllFiles treats that as a file-scope walk so CLI --all-files and
        // sibling-style AllFiles=true succeed. Default single-file scope
        // remains statement via NormalizeScope.
        if (@params.AllFiles && !string.IsNullOrWhiteSpace(@params.Scope) && scope == ScopeStatement)
        {
            throw new RefactoringException(
                ErrorCodes.MissingRequiredParam,
                "allFiles cannot be combined with scope=statement.");
        }

        if (@params.AllFiles && scope == ScopeType)
        {
            throw new RefactoringException(
                ErrorCodes.MissingRequiredParam,
                "allFiles cannot be combined with scope=type.");
        }

        if (@params.AllFiles)
        {
            if (@params.Line.HasValue && @params.Line.Value < 1)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

            if (@params.Column.HasValue && @params.Column.Value < 1)
                throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required when allFiles is false.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

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

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RemoveBracesParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var scope = BraceTypeNameHelpers.NormalizeScope(@params.Scope);
        HashSet<StatementSyntax>? onlyThese = null;
        TypeDeclarationSyntax? typeScope = null;
        SyntaxNode? previewOwner = null;

        if (scope == ScopeStatement)
        {
            var target = ControlTargetHelpers.FindControlTarget(CollectTargets(root), @params.Line!.Value, @params.Column);
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

            if (CannotBeEmbeddedStatement(block.Statements[0]))
            {
                throw new RefactoringException(
                    ErrorCodes.CompilationError,
                    "Removing these braces would produce a statement that cannot be embedded (CS1023).");
            }

            if (WouldCreateDanglingElse(target.Value.Owner, block.Statements[0]))
            {
                throw new RefactoringException(
                    ErrorCodes.CompilationError,
                    "Removing these braces would change how a following else binds (dangling else).");
            }

            if (GotoLabelHelpers.WouldHideExternallyReferencedLabel(block.Statements[0]))
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
            typeScope = BraceTypeNameHelpers.FindTypeDeclaration(root, @params.TypeName!);
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

    /// <summary>
    /// Applies file-scope brace removal to every C# document in the solution
    /// (same document filter as <c>FormatDocumentOperation</c> /
    /// <c>SimplifyNameOperation.ExecuteAllFilesAsync</c> /
    /// <c>AddBracesOperation.ExecuteAllFilesAsync</c>: <c>FilePath</c> ends
    /// with <c>.cs</c>). Files with nothing to unwrap are skipped. When every
    /// file is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        RemoveBracesParams @params,
        CancellationToken cancellationToken)
    {
        var currentSolution = Context.Solution;
        var allDocuments = currentSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;
        var unwrappedTotal = 0;

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDocument = currentSolution.GetDocument(document.Id) ?? document;
            if (currentDocument is SourceGeneratedDocument)
                continue;

            var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                continue;

            var rewriter = new BraceRewriter(onlyThese: null, typeScope: null, unwrapElseIf: false, previewOwner: null);
            var newRoot = rewriter.Visit(root) ?? root;
            if (rewriter.UnwrappedCount == 0)
                continue;

            newRoot = Formatter.Format(
                newRoot,
                BraceRewriter.FormatAnnotation,
                Context.Workspace,
                cancellationToken: cancellationToken);

            var newDocument = currentDocument.WithSyntaxRoot(newRoot);
            var beforeText = await currentDocument.GetTextAsync(cancellationToken);
            var afterText = await newDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            unwrappedTotal += rewriter.UnwrappedCount;

            if (@params.Preview)
            {
                var span = root.GetLocation().GetLineSpan();
                allPendingChanges.Add(new PendingChange
                {
                    File = currentDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = BuildDescription(ScopeFile, rewriter.UnwrappedCount, null),
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
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = allPendingChanges,
                StatementsModified = unwrappedTotal,
                Scope = ScopeFile
            };
        }

        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
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
                StatementsModified = unwrappedTotal,
                Scope = ScopeFile
            };
        }

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
            Scope = ScopeFile
        };
    }

    /// <summary>
    /// True when <paramref name="statement"/> cannot legally replace a
    /// single-statement block as an embedded statement (CS1023): local
    /// declarations, local functions, and labeled statements.
    /// </summary>
    internal static bool CannotBeEmbeddedStatement(StatementSyntax statement) =>
        statement is LocalDeclarationStatementSyntax
            or LocalFunctionStatementSyntax
            or LabeledStatementSyntax;

    /// <summary>
    /// True when unwrapping a block whose single statement is
    /// <paramref name="inner"/> would let a following <c>else</c> bind to a
    /// nested <c>if</c> (dangling else) or produce a second <c>else</c>.
    /// Only the <c>if</c> then-body can create that problem; an
    /// <see cref="ElseClauseSyntax"/> owner is the else itself.
    /// </summary>
    internal static bool WouldCreateDanglingElse(SyntaxNode owner, StatementSyntax inner)
    {
        if (owner is not IfStatementSyntax ifStatement || ifStatement.Else == null)
            return false;

        return EmbeddedStatementWouldCaptureFollowingElse(inner);
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
                            ifStatement.Else,
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

        var statement = block.Statements[0];
        var openTrivia = NonWhitespaceTrivia(block.OpenBraceToken.LeadingTrivia)
            .Concat(NonWhitespaceTrivia(block.OpenBraceToken.TrailingTrivia));
        var closeTrivia = NonWhitespaceTrivia(block.CloseBraceToken.LeadingTrivia)
            .Concat(NonWhitespaceTrivia(block.CloseBraceToken.TrailingTrivia));

        return statement
            .WithLeadingTrivia(openTrivia.Concat(statement.GetLeadingTrivia()))
            .WithTrailingTrivia(statement.GetTrailingTrivia().Concat(closeTrivia));
    }

    private static IEnumerable<SyntaxTrivia> NonWhitespaceTrivia(SyntaxTriviaList trivia) =>
        trivia.Where(item => !item.IsKind(SyntaxKind.WhitespaceTrivia)
            && !item.IsKind(SyntaxKind.EndOfLineTrivia));

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

            return AnnotateIfPreviewOwner(node, rewritten);
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

            if (CannotBeEmbeddedStatement(inner))
                return false;

            if (!_unwrapElseIf && owner is ElseClauseSyntax && inner is IfStatementSyntax)
                return false;

            if (WouldCreateDanglingElse(owner, inner))
                return false;

            if (GotoLabelHelpers.WouldHideExternallyReferencedLabel(inner))
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

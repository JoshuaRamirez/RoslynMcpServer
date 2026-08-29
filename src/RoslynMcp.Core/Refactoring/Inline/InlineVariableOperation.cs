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
    protected override void ValidateParams(InlineVariableParams @params) => Validate(@params);

    /// <summary>
    /// Validates inline-variable parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(InlineVariableParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

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

        // Line is required when more than one variable matches, even if
        // column is set. Column without Line is not a source position:
        // FindDeclarator would substitute each candidate's own start line
        // and could silently pick the shortest equally-aligned local.
        // When both are set, pick by identifier/declaration span and do
        // not require the declaration to start on `line` (continuation-
        // line identifier).
        if (variableDeclarators.Count > 1 && !@params.Line.HasValue)
        {
            var lines = variableDeclarators
                .Select(v => StartLine(v))
                .ToList();
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple variables named '{@params.VariableName}' found. Provide line number. Options: {string.Join(", ", lines)}");
        }

        var declarator = FindDeclarator(root, @params.VariableName, @params.Line, @params.Column);
        if (declarator == null)
        {
            var location = @params.Column.HasValue
                ? @params.Line.HasValue
                    ? $"'{@params.VariableName}' at line {@params.Line}, column {@params.Column.Value}"
                    : $"'{@params.VariableName}' at column {@params.Column.Value}"
                : $"'{@params.VariableName}' at line {@params.Line}";
            throw new RefactoringException(
                ErrorCodes.VariableNotFound,
                $"Variable {location} not found.");
        }

        // Remember the chosen declarator so removal after rewrite does
        // not drop a different same-name local.
        var targetSpanStart = declarator.SpanStart;

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

        // Remove the chosen declaration (not another same-name local).
        var newDeclarator = newRoot!.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v =>
                v.Identifier.Text == @params.VariableName && v.SpanStart == targetSpanStart);

        if (newDeclarator != null)
        {
            var newDeclaration = newDeclarator.Parent as VariableDeclarationSyntax;
            if (newDeclaration != null && newDeclaration.Variables.Count == 1)
            {
                var statementToRemove = newDeclarator.Ancestors()
                    .OfType<LocalDeclarationStatementSyntax>()
                    .FirstOrDefault();
                if (statementToRemove != null)
                {
                    newRoot = newRoot.RemoveNode(statementToRemove, SyntaxRemoveOptions.KeepLeadingTrivia);
                }
            }
            else
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

    /// <summary>
    /// Resolves the variable declarator. Omitted <paramref name="column"/>
    /// keeps today's first-match (VariableName; Line when more than one
    /// match, start-line filter). When set, picks the declaration whose
    /// identifier or declaration span covers that 1-based column.
    /// </summary>
    internal static VariableDeclaratorSyntax? FindDeclarator(
        SyntaxNode root,
        string variableName,
        int? line,
        int? column)
    {
        var declarators = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.Text == variableName)
            .ToList();

        if (column.HasValue)
        {
            // When column is set, do not require the declaration to start
            // on `line` — a split declaration's identifier may live on a
            // continuation line whose declaration span still covers that
            // column.
            return declarators
                .Where(d => DeclaratorCoversColumn(d, line ?? StartLine(d), column.Value))
                .OrderBy(d => IdentifierCoversColumn(d, line ?? StartLine(d), column.Value) ? 0 : 1)
                .ThenBy(d => d.Span.Length)
                .FirstOrDefault();
        }

        // Omitted column keeps today's VariableName + Line pick: a single
        // name match is used as-is (Line is only for disambiguation).
        // More than one match uses the first whose declaration starts on
        // `line`.
        if (declarators.Count <= 1)
            return declarators.FirstOrDefault();

        if (!line.HasValue)
            return null;

        return declarators.FirstOrDefault(d => StartLine(d) == line.Value);
    }

    private static int StartLine(VariableDeclaratorSyntax declarator) =>
        declarator.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool DeclaratorCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        IdentifierCoversColumn(declarator, line, column) ||
        SpanCoversColumn(DeclarationSpan(declarator), line, column);

    private static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        SpanCoversColumn(declarator.Identifier.GetLocation().GetLineSpan(), line, column);

    private static FileLinePositionSpan DeclarationSpan(VariableDeclaratorSyntax declarator) =>
        declarator.Parent is VariableDeclarationSyntax declaration
            ? declaration.GetLocation().GetLineSpan()
            : declarator.GetLocation().GetLineSpan();

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent declaration also
    /// match the previous one.
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

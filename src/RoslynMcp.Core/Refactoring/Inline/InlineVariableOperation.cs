using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// Inlines a local variable by replacing all usages with its initializer value.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and inlines every eligible local, skipping ineligible
/// locals rather than throwing.
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
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.VariableName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with variableName, line, or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        var sourceFile = @params.SourceFile!;

        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");

        if (@params.Line.HasValue && @params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        InlineVariableParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var variableName = @params.VariableName!;

        var document = GetDocumentOrThrow(sourceFile);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find variable declaration
        var variableDeclarators = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.Text == variableName)
            .ToList();

        if (variableDeclarators.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.VariableNotFound,
                $"Variable '{variableName}' not found.");
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
                $"Multiple variables named '{variableName}' found. Provide line number. Options: {string.Join(", ", lines)}");
        }

        var declarator = FindDeclarator(root, variableName, @params.Line, @params.Column);
        if (declarator == null)
        {
            var location = @params.Column.HasValue
                ? @params.Line.HasValue
                    ? $"'{variableName}' at line {@params.Line}, column {@params.Column.Value}"
                    : $"'{variableName}' at column {@params.Column.Value}"
                : $"'{variableName}' at line {@params.Line}";
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
                if (id.Identifier.Text != variableName) return false;

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
                if (id.Identifier.Text != variableName) return false;

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
            return CreatePreviewResult(operationId, sourceFile, variableName, initializerExpression, usages.Count);
        }

        // Apply changes: replace all usages with initializer, remove declaration
        var rewriter = new InlineRewriter(variableName, variableSymbol, initializerExpression, semanticModel);
        var newRoot = rewriter.Visit(root);

        // Remove the chosen declaration (not another same-name local).
        var newDeclarator = newRoot!.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v =>
                v.Identifier.Text == variableName && v.SpanStart == targetSpanStart);

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
                Name = variableName,
                FullyQualifiedName = variableName,
                Kind = Contracts.Enums.SymbolKind.Local
            },
            usages.Count,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>InlineConstantOperation.ExecuteAllFilesAsync</c>
    /// / <c>GenerateMethodStubOperation.ExecuteAllFilesAsync</c>) and inlines
    /// every eligible local <see cref="VariableDeclaratorSyntax"/> in a
    /// <see cref="LocalDeclarationStatementSyntax"/> inside a method.
    /// Optional <c>sourceFile</c> limits via <see cref="DocumentSourceFileFilter"/>.
    /// Linked documents that share a physical path are rewritten once and the
    /// same text is applied to every sibling <see cref="DocumentId"/>
    /// (<see cref="PathResolver.GetPathComparisonKey"/>). Ineligible locals
    /// (no initializer, side effects, reassignment, ref/out, not in a method,
    /// uneditable / source-generated docs) are skipped rather than failing the
    /// walk. Deterministic <c>SpanStart</c> order within a file. When every
    /// file is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        InlineVariableParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        // One physical path may appear as multiple Documents when linked into
        // several projects. Rewrite once per normalized path and apply the same
        // text to every sibling DocumentId (ConvertToBlockBody allFiles / Codex).
        var documentGroups = allDocuments
            .GroupBy(d => PathResolver.GetPathComparisonKey(d.FilePath!), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                .ToList())
            .OrderBy(group => group[0].FilePath, StringComparer.Ordinal)
            .ToList();

        var inlinedCountByDoc = new Dictionary<DocumentId, int>();

        foreach (var linkedDocuments in documentGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var primary = linkedDocuments.FirstOrDefault(d =>
                d is not SourceGeneratedDocument &&
                DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace));
            if (primary == null)
                continue;

            while (true)
            {
                var currentDocument = currentSolution.GetDocument(primary.Id);
                if (currentDocument == null ||
                    currentDocument is SourceGeneratedDocument ||
                    !DocumentEditableHelpers.IsDocumentEditable(currentDocument, Context.Workspace))
                {
                    break;
                }

                var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null)
                    break;

                Solution? updated = null;
                foreach (var declarator in CollectLocalDeclarators(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        updated = await TryInlineOneAsync(
                            currentDocument,
                            root,
                            semanticModel,
                            declarator,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        // Skip ineligible locals rather than failing the walk.
                        updated = null;
                    }

                    if (updated != null)
                        break;
                }

                if (updated == null)
                    break;

                var updatedPrimary = updated.GetDocument(primary.Id)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
                var afterText = await updatedPrimary.GetTextAsync(cancellationToken);

                currentSolution = updated;
                foreach (var linked in linkedDocuments)
                {
                    if (linked.Id == primary.Id)
                        continue;

                    var sibling = currentSolution.GetDocument(linked.Id);
                    if (sibling == null || sibling is SourceGeneratedDocument)
                        continue;
                    if (!DocumentEditableHelpers.IsDocumentEditable(sibling, Context.Workspace))
                        continue;

                    currentSolution = currentSolution.WithDocumentText(sibling.Id, afterText);
                }

                inlinedCountByDoc[primary.Id] =
                    inlinedCountByDoc.GetValueOrDefault(primary.Id) + 1;
            }
        }

        var documentsToCompare = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;
        var previewedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documentsToCompare)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalDocument = originalSolution.GetDocument(document.Id);
            var currentDocument = currentSolution.GetDocument(document.Id);
            if (originalDocument == null || currentDocument == null)
                continue;

            var beforeText = await originalDocument.GetTextAsync(cancellationToken);
            var afterText = await currentDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var pathKey = PathResolver.GetPathComparisonKey(originalDocument.FilePath!);
                if (!previewedPaths.Add(pathKey))
                    continue;

                var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
                var currentRoot = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                if (originalRoot == null || currentRoot == null)
                    continue;

                var span = originalRoot.GetLocation().GetLineSpan();
                var inlinedCount = inlinedCountByDoc.GetValueOrDefault(document.Id);
                // Count may live on a sibling DocumentId in the same path group.
                if (inlinedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        inlinedCount = Math.Max(inlinedCount, inlinedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = inlinedCount > 0
                        ? BuildAllFilesDescription(inlinedCount)
                        : "Update references of inlined variables",
                    BeforeSnippet = originalRoot.NormalizeWhitespace().ToFullString().Trim(),
                    AfterSnippet = currentRoot.NormalizeWhitespace().ToFullString().Trim(),
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                });
                continue;
            }

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

    /// <summary>
    /// Preview description for a file that inlined
    /// <paramref name="inlinedCount"/> variables.
    /// </summary>
    internal static string BuildAllFilesDescription(int inlinedCount) =>
        inlinedCount == 1
            ? "Inline variable"
            : $"Inline {inlinedCount} variables";

    /// <summary>
    /// Collects every local <see cref="VariableDeclaratorSyntax"/> in
    /// <paramref name="root"/> whose parent is a
    /// <see cref="LocalDeclarationStatementSyntax"/> (fields and for-loop
    /// declarators stay excluded). Deterministic <c>SpanStart</c> then
    /// span-length order.
    /// </summary>
    internal static IReadOnlyList<VariableDeclaratorSyntax> CollectLocalDeclarators(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(IsLocalDeclarator)
            .OrderBy(declarator => declarator.SpanStart)
            .ThenBy(declarator => declarator.Span.Length)
            .ToList();

    private static bool IsLocalDeclarator(VariableDeclaratorSyntax declarator) =>
        declarator.Parent is VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax };

    private async Task<Solution?> TryInlineOneAsync(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        VariableDeclaratorSyntax declarator,
        CancellationToken cancellationToken)
    {
        if (declarator.Initializer == null)
            return null;

        var initializerExpression = declarator.Initializer.Value;
        if (!MemberAnalyzer.IsSafeToInline(initializerExpression, semanticModel))
            return null;

        var variableSymbol = semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
        if (variableSymbol == null)
            return null;

        var containingMethod = declarator.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null)
            return null;

        var variableName = declarator.Identifier.Text;
        var targetSpanStart = declarator.SpanStart;

        var usages = containingMethod.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id =>
            {
                if (id.Identifier.Text != variableName) return false;
                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                return SymbolEqualityComparer.Default.Equals(symbol, variableSymbol);
            })
            .ToList();

        foreach (var usage in usages)
        {
            if (usage.Parent is ArgumentSyntax arg &&
                (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
                 arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
            {
                return null;
            }
        }

        var assignments = containingMethod.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a =>
            {
                if (a.Left is not IdentifierNameSyntax id) return false;
                if (id.Identifier.Text != variableName) return false;
                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                return SymbolEqualityComparer.Default.Equals(symbol, variableSymbol);
            })
            .ToList();

        if (assignments.Count > 0)
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        var rewriter = new InlineRewriter(variableName, variableSymbol, initializerExpression, semanticModel);
        var newRoot = rewriter.Visit(root);
        if (newRoot == null)
            return null;

        var newDeclarator = newRoot.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v =>
                v.Identifier.Text == variableName && v.SpanStart == targetSpanStart);

        if (newDeclarator != null)
        {
            var newDeclaration = newDeclarator.Parent as VariableDeclarationSyntax;
            if (newDeclaration != null && newDeclaration.Variables.Count == 1)
            {
                var statementToRemove = newDeclarator.Ancestors()
                    .OfType<LocalDeclarationStatementSyntax>()
                    .FirstOrDefault();
                if (statementToRemove != null)
                    newRoot = newRoot.RemoveNode(statementToRemove, SyntaxRemoveOptions.KeepLeadingTrivia) ?? newRoot;
            }
            else
            {
                newRoot = newRoot.RemoveNode(newDeclarator, SyntaxRemoveOptions.KeepNoTrivia) ?? newRoot;
            }
        }

        var beforeText = await document.GetTextAsync(cancellationToken);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var afterText = await newDocument.GetTextAsync(cancellationToken);
        if (beforeText.ContentEquals(afterText))
            return null;

        return newDocument.Project.Solution;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string sourceFile,
        string variableName,
        ExpressionSyntax initializer,
        int usageCount)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = sourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Inline variable '{variableName}' ({usageCount} usages replaced)",
                BeforeSnippet = $"var {variableName} = {initializer.ToFullString().Trim()};\n// ... {variableName} ...",
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
                .OrderBy(d => LocalCoverage.IdentifierCoversColumn(d, line ?? StartLine(d), column.Value) ? 0 : 1)
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
        LocalCoverage.IdentifierCoversColumn(declarator, line, column) ||
        SpanCoverage.SpanCoversColumn(DeclarationSpan(declarator), line, column);

    private static FileLinePositionSpan DeclarationSpan(VariableDeclaratorSyntax declarator) =>
        declarator.Parent is VariableDeclarationSyntax declaration
            ? declaration.GetLocation().GetLineSpan()
            : declarator.GetLocation().GetLineSpan();


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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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

        SourceFilePathHelpers.ValidateSourceFilePath(sourceFile);

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");

        if (@params.Line.HasValue && @params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (!SyntaxIdentifierValidation.IsValidIdentifier(@params.VariableName!))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"'{@params.VariableName}' is not a valid variable name.");
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
                .Select(v => SyntaxLineHelpers.StartLine(v))
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

        if (UsingDeclarationHelpers.IsUsingDeclaration(declarator))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot inline a using declaration (disposal would be lost).");
        }

        if (IsRefLocal(variableSymbol, initializerExpression))
        {
            throw new RefactoringException(
                ErrorCodes.UsedInRefContext,
                "Cannot inline a ref local.");
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

        // Align single-site eligibility with allFiles skip rules.
        foreach (var usage in usages)
        {
            if (IsByReferenceUsage(usage))
            {
                throw new RefactoringException(
                    ErrorCodes.UsedInRefContext,
                    "Cannot inline variable used in ref/out/in or by-reference context.");
            }

            if (MethodSymbolHelpers.IsInNameof(usage))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidSelection,
                    "Cannot inline variable used in nameof(...).");
            }

            if (IsInferredAnonymousObjectMember(usage))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidSelection,
                    "Cannot inline variable used as an inferred anonymous-object member name.");
            }
        }

        if (IsReassigned(containingMethod, variableName, variableSymbol, initializerExpression, semanticModel, cancellationToken))
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
    /// Optional <c>sourceFile</c> limits via
    /// <see cref="AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile"/>.
    /// Linked documents that share a physical path are rewritten once and the
    /// same text is applied to every sibling <see cref="DocumentId"/>
    /// (<see cref="PathResolver.GetPathComparisonKey"/>). Ineligible locals
    /// (no initializer, side effects, reassignment including ++/--, ref/out/in
    /// or <c>ref</c> expression, nameof, not in a method, uneditable /
    /// source-generated docs) are skipped rather than failing the
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
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        // One physical path may appear as multiple Documents when linked into
        // several projects. Rewrite once per normalized path and apply the same
        // text to every sibling DocumentId (ConvertToBlockBody allFiles / Codex).
        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

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
                foreach (var declarator in LocalDeclaratorHelpers.CollectLocalDeclarators(root))
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

        var documentsToCompare = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

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

        if (UsingDeclarationHelpers.IsUsingDeclaration(declarator) || IsRefLocal(variableSymbol, initializerExpression))
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
            if (IsByReferenceUsage(usage) ||
                MethodSymbolHelpers.IsInNameof(usage) ||
                IsInferredAnonymousObjectMember(usage))
            {
                return null;
            }
        }

        if (IsReassigned(containingMethod, variableName, variableSymbol, initializerExpression, semanticModel, cancellationToken))
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
    /// True when <paramref name="usage"/> is passed as <c>ref</c>/<c>out</c>/<c>in</c>
    /// or appears under a <see cref="RefExpressionSyntax"/> (ref alias / <c>return ref</c>).
    /// </summary>
    private static bool IsByReferenceUsage(IdentifierNameSyntax usage)
    {
        if (usage.Parent is ArgumentSyntax arg &&
            (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
             arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword) ||
             arg.RefOrOutKeyword.IsKind(SyntaxKind.InKeyword)))
        {
            return true;
        }

        // covers `return ref x`, `ref int alias = ref x`, etc.
        if (usage.Parent is RefExpressionSyntax)
            return true;

        // &x address-of
        if (usage.Parent is PrefixUnaryExpressionSyntax prefix &&
            prefix.IsKind(SyntaxKind.AddressOfExpression))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True for ref/ref-readonly locals or initializers that are
    /// <see cref="RefExpressionSyntax"/> (<c>ref int alias = ref value</c>).
    /// </summary>
    private static bool IsRefLocal(ILocalSymbol variableSymbol, ExpressionSyntax initializer) =>
        variableSymbol.RefKind != RefKind.None ||
        initializer is RefExpressionSyntax;

    /// <summary>
    /// True when <paramref name="usage"/> is an inferred anonymous-object
    /// member (<c>new { x }</c>), which cannot be rewritten to a literal.
    /// </summary>
    private static bool IsInferredAnonymousObjectMember(IdentifierNameSyntax usage) =>
        usage.Parent is AnonymousObjectMemberDeclaratorSyntax member &&
        member.NameEquals == null;

    /// <summary>
    /// True when the local is written after initialization, either directly or through
    /// a receiver chain (see <see cref="ClassifyWrite"/>). A direct write (assignment,
    /// deconstruction element, <c>++</c>/<c>--</c>) always refuses. A write through the local
    /// as a receiver (<c>local.X = v</c>, <c>((T)local)[i] = v</c>, <c>local.M().X++</c>)
    /// refuses unless the initializer is the same object at every use site
    /// (<see cref="InitializerIsSharedReference"/>): writing a field of a struct copy must stay
    /// refused (#2462), and so must a write that would land on a re-created object (#3090 review).
    /// A local read inside an index or argument (<c>values[index] = 10</c>) is not a write (#3090).
    /// </summary>
    private static bool IsReassigned(
        BaseMethodDeclarationSyntax containingMethod,
        string variableName,
        ILocalSymbol variableSymbol,
        ExpressionSyntax initializer,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        bool? shared = null;
        foreach (var id in containingMethod.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.Text != variableName)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(id, cancellationToken).Symbol, variableSymbol))
                continue;

            switch (ClassifyWrite(id, semanticModel, cancellationToken))
            {
                case WriteKind.Direct:
                    return true;
                case WriteKind.ThroughReceiver:
                    shared ??= InitializerIsSharedReference(initializer, variableSymbol, semanticModel, cancellationToken);
                    if (!shared.Value)
                        return true;
                    break;
            }
        }

        return false;
    }

    private enum WriteKind { None, Direct, ThroughReceiver }

    /// <summary>
    /// Classifies one use of the local with a closed allowlist over the parent of each node on
    /// the way up (#3090 review, round 5). Every parent kind is one of three things:
    /// <list type="bullet">
    /// <item><b>Transparent</b>: the node still denotes the local's storage or its value flows
    /// on, so the walk continues. These are parentheses, <c>!</c>, <c>checked</c>/<c>unchecked</c>,
    /// casts, <c>as</c>, <c>?:</c> branches, <c>??</c> operands, switch-expression arms, the right
    /// side of any assignment (<c>=</c>, <c>??=</c>, <c>+=</c>, ...), deconstruction tuples, and as
    /// a receiver: member / element / <c>?.</c> access, invocation and pointer indirection.</item>
    /// <item><b>Write</b>: an assignment target, a <c>++</c>/<c>--</c> operand, a <c>ref</c>/<c>out</c>
    /// argument, a <c>ref</c> expression, <c>&amp;</c>, <c>__makeref</c>, a foreach deconstruction
    /// target, or a <c>?.</c> on the chain whose right side writes.</item>
    /// <item><b>Pure read</b>: a by-value or <c>in</c> argument or index, an operand of a built-in
    /// unary / binary operator, a condition or governing expression, a statement, return, yield,
    /// throw or initializer value, an interpolation, a collection / object initializer element,
    /// <c>await</c>, a pattern-matched expression, a range bound, a <c>with</c> source, a lambda
    /// body or a query clause.</item>
    /// </list>
    /// Any other parent is unknown and counts as a direct write, so a syntax kind nobody listed
    /// refuses rather than silently becoming a read. Direct by-reference uses are refused earlier
    /// with UsedInRefContext. A call argument that can hold a reference (judged by the argument
    /// expression's type) inside an assignment target or by-reference argument may be the object
    /// written (<c>Ext.IdA(arr)[0] = 1</c>), so it counts as a receiver write.
    /// </summary>
    private static WriteKind ClassifyWrite(IdentifierNameSyntax id, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        SyntaxNode current = id;
        var throughReceiver = false;
        while (true)
        {
            var parent = current.Parent;
            switch (parent)
            {
                // Transparent: same storage, or the value flows on.
                case ParenthesizedExpressionSyntax:
                case CheckedExpressionSyntax:
                case PostfixUnaryExpressionSyntax suppress when suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                case CastExpressionSyntax cast when cast.Expression == current:
                case BinaryExpressionSyntax asExpression when asExpression.IsKind(SyntaxKind.AsExpression) && asExpression.Left == current:
                case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                case ConditionalExpressionSyntax conditional when conditional.Condition != current:
                case AssignmentExpressionSyntax assignedValue when assignedValue.Right == current:
                    current = parent;
                    continue;
                case SwitchExpressionArmSyntax arm when arm.Expression == current && arm.Parent is SwitchExpressionSyntax switchExpression:
                    current = switchExpression;
                    continue;
                case ArgumentSyntax { Parent: TupleExpressionSyntax tuple } tupleArgument when tupleArgument.Expression == current:
                    current = tuple;
                    continue;

                // Transparent as a receiver.
                case MemberAccessExpressionSyntax member when member.Expression == current:
                case ElementAccessExpressionSyntax element when element.Expression == current:
                case InvocationExpressionSyntax invocation when invocation.Expression == current:
                case PrefixUnaryExpressionSyntax indirection when indirection.IsKind(SyntaxKind.PointerIndirectionExpression):
                    throughReceiver = true;
                    current = parent;
                    continue;
                case ConditionalAccessExpressionSyntax conditionalAccess when conditionalAccess.Expression == current:
                    if (conditionalAccess.WhenNotNull.DescendantNodesAndSelf().Any(IsWriteExpression))
                        return WriteKind.ThroughReceiver;
                    throughReceiver = true;
                    current = parent;
                    continue;
            }

            break;
        }

        var parentNode = current.Parent;
        if (IsWriteParent(parentNode, current))
            return throughReceiver ? WriteKind.ThroughReceiver : WriteKind.Direct;

        if (!IsPureReadParent(parentNode, current))
            return WriteKind.Direct;

        if (parentNode is ArgumentSyntax { Parent: ArgumentListSyntax } escape &&
            current is ExpressionSyntax argumentExpression &&
            semanticModel.GetTypeInfo(argumentExpression, cancellationToken).Type is not { IsUnmanagedType: true } &&
            IsInsideWriteTarget(escape))
        {
            return WriteKind.ThroughReceiver;
        }

        return WriteKind.None;
    }

    private static bool IsWriteParent(SyntaxNode? parent, SyntaxNode current) => parent switch
    {
        AssignmentExpressionSyntax assignment => assignment.Left == current,
        PrefixUnaryExpressionSyntax prefix => prefix.Operand == current &&
            (IsIncrementOrDecrement(prefix) || prefix.IsKind(SyntaxKind.AddressOfExpression)),
        PostfixUnaryExpressionSyntax postfix => postfix.Operand == current && IsIncrementOrDecrement(postfix),
        ArgumentSyntax argument => IsByReferenceArgument(argument),
        RefExpressionSyntax => true,
        MakeRefExpressionSyntax => true,
        ForEachVariableStatementSyntax forEach => forEach.Variable == current,
        _ => false
    };

    private static bool IsByReferenceArgument(ArgumentSyntax argument) =>
        argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword);

    private static bool IsPureReadParent(SyntaxNode? parent, SyntaxNode current) => parent switch
    {
        ArgumentSyntax { Parent: ArgumentListSyntax or BracketedArgumentListSyntax } argument => !IsByReferenceArgument(argument),
        PrefixUnaryExpressionSyntax prefix => prefix.Kind() is SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression
            or SyntaxKind.LogicalNotExpression or SyntaxKind.BitwiseNotExpression or SyntaxKind.IndexExpression,
        BinaryExpressionSyntax binary => !binary.IsKind(SyntaxKind.AsExpression) && !binary.IsKind(SyntaxKind.CoalesceExpression),
        ConditionalExpressionSyntax conditional => conditional.Condition == current,
        SwitchExpressionSyntax switchExpression => switchExpression.GoverningExpression == current,
        IsPatternExpressionSyntax isPattern => isPattern.Expression == current,
        RangeExpressionSyntax or AwaitExpressionSyntax or WhenClauseSyntax => true,
        WithExpressionSyntax with => with.Expression == current,
        ExpressionStatementSyntax or ReturnStatementSyntax or YieldStatementSyntax or ThrowStatementSyntax
            or ThrowExpressionSyntax or EqualsValueClauseSyntax or ArrowExpressionClauseSyntax => true,
        IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax
            or ForEachStatementSyntax or SwitchStatementSyntax or LockStatementSyntax or UsingStatementSyntax => true,
        InterpolationSyntax or InterpolationAlignmentClauseSyntax => true,
        InitializerExpressionSyntax or AnonymousObjectMemberDeclaratorSyntax or ArrayRankSpecifierSyntax
            or ExpressionElementSyntax or SpreadElementSyntax => true,
        AnonymousFunctionExpressionSyntax lambda => lambda.ExpressionBody == current,
        FromClauseSyntax or LetClauseSyntax or WhereClauseSyntax or SelectClauseSyntax or JoinClauseSyntax
            or OrderingSyntax or GroupClauseSyntax => true,
        _ => false
    };

    /// <summary>
    /// True when <paramref name="node"/> sits inside an assignment target, a <c>++</c>/<c>--</c>
    /// operand or a <c>ref</c>/<c>out</c> argument within its own statement.
    /// </summary>
    private static bool IsInsideWriteTarget(SyntaxNode node)
    {
        for (var current = node; current.Parent is { } parent; current = parent)
        {
            if (parent is StatementSyntax or AnonymousFunctionExpressionSyntax or MemberDeclarationSyntax)
                return false;
            if (IsWriteParent(parent, current))
                return true;
        }

        return false;
    }

    private static bool IsWriteExpression(SyntaxNode node) =>
        node is AssignmentExpressionSyntax ||
        (node is PrefixUnaryExpressionSyntax prefix && IsIncrementOrDecrement(prefix)) ||
        (node is PostfixUnaryExpressionSyntax postfix && IsIncrementOrDecrement(postfix));

    private static bool IsIncrementOrDecrement(ExpressionSyntax node) =>
        node.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression;

    /// <summary>
    /// True when writing through the local and through the inlined initializer at every use site
    /// is certain to hit the same object. This is an allowlist over the initializer's
    /// <see cref="IOperation"/>: the local is a reference type, the initializer converts to it by
    /// identity or an implicit reference conversion, and the initializer is only locals,
    /// parameters, <c>this</c>/<c>base</c>, fields, array elements at constant indices, and
    /// built-in reference conversions (casts, <c>as</c>) of those. Everything else is refused as
    /// possibly fresh: object / array / collection creation, <c>with</c>, user-defined operators
    /// and conversions (a call, e.g. <c>-_w</c>, <c>_w + _w</c>), property or indexer getters,
    /// method calls, conditionals and <c>??</c> (#3090 review).
    /// </summary>
    private static bool InitializerIsSharedReference(
        ExpressionSyntax initializer,
        ILocalSymbol variableSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!variableSymbol.Type.IsReferenceType)
            return false;

        var conversion = semanticModel.ClassifyConversion(initializer, variableSymbol.Type);
        if (!conversion.Exists ||
            !(conversion.IsIdentity || (conversion.IsImplicit && conversion.IsReference)) ||
            conversion.IsBoxing ||
            conversion.IsUserDefined)
        {
            return false;
        }

        var operation = semanticModel.GetOperation(initializer, cancellationToken);
        return operation != null && IsSharedReferenceOperation(operation);
    }

    private static bool IsSharedReferenceOperation(IOperation operation) => operation switch
    {
        ILocalReferenceOperation or IParameterReferenceOperation or IInstanceReferenceOperation => true,
        IFieldReferenceOperation field => field.Instance == null || IsSharedReferenceOperation(field.Instance),
        IArrayElementReferenceOperation element =>
            IsSharedReferenceOperation(element.ArrayReference) &&
            element.Indices.All(i => i.ConstantValue.HasValue),
        IConversionOperation conversion => conversion.OperatorMethod == null &&
            conversion.GetConversion() is { IsUserDefined: false, IsBoxing: false, IsUnboxing: false } csharpConversion &&
            (csharpConversion.IsIdentity || csharpConversion.IsReference) &&
            IsSharedReferenceOperation(conversion.Operand),
        _ => false
    };

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
                .Where(d => DeclaratorCoversColumn(d, line ?? SyntaxLineHelpers.StartLine(d), column.Value))
                .OrderBy(d => LocalCoverage.IdentifierCoversColumn(d, line ?? SyntaxLineHelpers.StartLine(d), column.Value) ? 0 : 1)
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

        return declarators.FirstOrDefault(d => SyntaxLineHelpers.StartLine(d) == line.Value);
    }

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

            // nameof(x) is not a value use; rewriting yields nameof(1).
            if (MethodSymbolHelpers.IsInNameof(node))
            {
                return base.VisitIdentifierName(node);
            }

            // Wrap in parentheses if needed for precedence / call/member syntax.
            // Anonymous functions especially need parens when used as f() / f.M.
            var needsParens = node.Parent is BinaryExpressionSyntax ||
                              node.Parent is ConditionalExpressionSyntax;

            // As the receiver of .M, [i], (), ?. or a postfix operator, anything but a primary
            // expression would bind wrongly: (Derived)_base.X, -_i.ToString(), new int[3][0],
            // a ?? b! (#3090 review). Parenthesize every non-primary initializer there.
            var isReceiver = node.Parent switch
            {
                MemberAccessExpressionSyntax member => member.Expression == node,
                ElementAccessExpressionSyntax element => element.Expression == node,
                InvocationExpressionSyntax invocation => invocation.Expression == node,
                ConditionalAccessExpressionSyntax conditional => conditional.Expression == node,
                PostfixUnaryExpressionSyntax => true,
                _ => false
            };

            // A prefix-unary initializer pasted right after a prefix-unary or binary operator would
            // merge with it (var neg = -a; return -neg; → return --a;, a-neg → a--a), silently
            // turning a negation into a decrement or breaking the build, so parenthesize it:
            // -(-a), a-(-a) (#3121).
            var mergesWithOperator = _replacement is PrefixUnaryExpressionSyntax &&
                                     node.Parent is PrefixUnaryExpressionSyntax or BinaryExpressionSyntax;

            // In an interpolation hole a bare ?: ends the expression at its ':', which starts the
            // format clause ({count == 1 ? "item" : "items"} → CS8361), so parenthesize it (#3040).
            var conditionalInHole = node.Parent is InterpolationSyntax &&
                                    _replacement is ConditionalExpressionSyntax;

            if (mergesWithOperator || conditionalInHole ||
                (isReceiver && !IsPrimaryExpression(_replacement)) ||
                (needsParens &&
                 (_replacement is BinaryExpressionSyntax or
                  ConditionalExpressionSyntax or
                  AnonymousFunctionExpressionSyntax)))
            {
                return SyntaxFactory.ParenthesizedExpression(_replacement)
                    .WithTriviaFrom(node);
            }

            return _replacement.WithTriviaFrom(node);
        }

        private static bool IsPrimaryExpression(ExpressionSyntax expression) =>
            expression is SimpleNameSyntax
                or QualifiedNameSyntax
                or AliasQualifiedNameSyntax
                or MemberAccessExpressionSyntax
                or ElementAccessExpressionSyntax
                or InvocationExpressionSyntax
                or LiteralExpressionSyntax
                or ThisExpressionSyntax
                or BaseExpressionSyntax
                or ParenthesizedExpressionSyntax
                or TupleExpressionSyntax
                or ObjectCreationExpressionSyntax
                or AnonymousObjectCreationExpressionSyntax
                or TypeOfExpressionSyntax
                or DefaultExpressionSyntax
                or SizeOfExpressionSyntax
                or CheckedExpressionSyntax
                or InterpolatedStringExpressionSyntax;
    }
}

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

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Promotes a local variable or expression to a class field, optionally
/// initializing the field in a constructor.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and promotes every eligible local, naming each field
/// from that local and skipping ineligible sites rather than throwing.
/// </summary>
public sealed class IntroduceFieldOperation : RefactoringOperationBase<IntroduceFieldParams>
{
    /// <summary>
    /// Creates a new introduce-field operation.
    /// </summary>
    public IntroduceFieldOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(IntroduceFieldParams @params) => Validate(@params);

    /// <summary>
    /// Validates introduce-field parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(IntroduceFieldParams @params)
    {
        if (@params.AllFiles)
        {
            if (@params.StartLine.HasValue ||
                @params.StartColumn.HasValue ||
                @params.EndLine.HasValue ||
                @params.EndColumn.HasValue ||
                !string.IsNullOrWhiteSpace(@params.FieldName))
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with startLine, startColumn, endLine, endColumn, or fieldName.");
            }

            // Optional sourceFile still must be an absolute .cs path when set
            // (ChangeSignature allFiles / Copilot).
            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            {
                ValidateSourceFilePath(@params.SourceFile!);
                if (!File.Exists(@params.SourceFile!))
                    throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.FieldName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "fieldName is required.");

        if (!@params.StartLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startLine is required.");

        if (!@params.StartColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startColumn is required.");

        if (!@params.EndLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endLine is required.");

        if (!@params.EndColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endColumn is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (@params.StartLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.StartColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (@params.EndLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.EndColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (@params.EndLine.Value < @params.StartLine.Value ||
            (@params.EndLine.Value == @params.StartLine.Value && @params.EndColumn.Value < @params.StartColumn.Value))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        if (!IsValidIdentifier(@params.FieldName!))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid field name: {@params.FieldName}");

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static void ValidateSourceFilePath(string sourceFile)
    {
        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        IntroduceFieldParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var fieldName = @params.FieldName!;

        var document = GetDocumentOrThrow(sourceFile);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = GetSelectionSpan(sourceText, @params);
        var node = root.FindNode(span);

        var plan = BuildPlan(node, span, semanticModel, @params, cancellationToken);

        if (@params.Preview)
            return CreatePreviewResult(operationId, sourceFile, fieldName, @params, plan);

        var newRoot = ApplyPlan((CompilationUnitSyntax)root, plan, @params);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

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
                Name = fieldName,
                FullyQualifiedName = $"{plan.ContainingTypeName}.{fieldName}",
                Kind = Contracts.Enums.SymbolKind.Field
            },
            plan.ReplacementCount,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>IntroduceParameterOperation.ExecuteAllFilesAsync</c>
    /// / <c>InlineVariableOperation.ExecuteAllFilesAsync</c>) and promotes
    /// every eligible local <see cref="VariableDeclaratorSyntax"/> in a
    /// <see cref="LocalDeclarationStatementSyntax"/> to a field named from
    /// that local. Optional <c>sourceFile</c> limits via
    /// <see cref="DocumentSourceFileFilter"/>. Linked documents that share a
    /// physical path are rewritten once and the same text is applied to every
    /// sibling <see cref="DocumentId"/> via <see cref="PathResolver.GetPathComparisonKey"/>.
    /// Expression-only (non-local) sites, uneditable / source-generated docs,
    /// name collisions, unsupported captures, using declarations, const locals,
    /// ref / ref-readonly locals, locals whose types use method type parameters,
    /// inline initializers that capture instance members (when not initializing
    /// in a constructor), readonly sites written after initialization
    /// (including ref/out), and otherwise ineligible locals are skipped rather
    /// than failing the walk. Deterministic
    /// <c>SpanStart</c> order within a file. When every file is a no-op,
    /// succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        IntroduceFieldParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var promotedCountByDoc = new Dictionary<DocumentId, int>();

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
                        updated = await TryIntroduceOneAsync(
                            currentDocument,
                            root,
                            semanticModel,
                            declarator,
                            @params,
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

                var beforeSolution = currentSolution;
                currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                    beforeSolution,
                    updated,
                    Context.Workspace,
                    cancellationToken);

                promotedCountByDoc[primary.Id] =
                    promotedCountByDoc.GetValueOrDefault(primary.Id) + 1;
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
                var promotedCount = promotedCountByDoc.GetValueOrDefault(document.Id);
                if (promotedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        promotedCount = Math.Max(promotedCount, promotedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = promotedCount > 0
                        ? BuildAllFilesDescription(promotedCount)
                        : "Update introduce_field rewrites",
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

    private static List<Document> FilterAllFilesDocumentsBySourceFile(List<Document> documents, string sourceFile)
    {
        var normalizedSourceFile = PathResolver.NormalizePath(sourceFile);
        var sourceFileKey = PathResolver.GetPathComparisonKey(sourceFile);
        var exactMatches = documents
            .Where(d => string.Equals(PathResolver.NormalizePath(d.FilePath!), normalizedSourceFile, StringComparison.Ordinal))
            .ToList();
        if (exactMatches.Count > 0)
        {
            var exactKeys = exactMatches
                .Select(d => PathResolver.GetPathComparisonKey(d.FilePath!))
                .ToHashSet(StringComparer.Ordinal);
            return documents
                .Where(d => exactKeys.Contains(PathResolver.GetPathComparisonKey(d.FilePath!)))
                .ToList();
        }

        var matchedDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(documents, normalizedSourceFile);
        var distinctPaths = matchedDocuments
            .Select(d => PathResolver.GetPathComparisonKey(d.FilePath!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return distinctPaths.Count switch
        {
            0 when !File.Exists(sourceFile) => throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"Source file not found: {sourceFile}"),
            0 => throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"File not found in workspace: {sourceFile}"),
            > 1 => throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"Multiple workspace files match path ignoring case: {sourceFile}. Use the exact file path casing."),
            _ when !string.Equals(distinctPaths[0], sourceFileKey, StringComparison.Ordinal) && !File.Exists(sourceFile) =>
                throw new RefactoringException(
                    ErrorCodes.SourceFileNotFound,
                    $"Source file not found: {sourceFile}"),
            _ => matchedDocuments
        };
    }

    /// <summary>
    /// Preview description for a file that introduced
    /// <paramref name="promotedCount"/> fields.
    /// </summary>
    internal static string BuildAllFilesDescription(int promotedCount) =>
        promotedCount == 1
            ? "Introduce field"
            : $"Introduce {promotedCount} fields";

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
            .Where(declarator =>
                declarator.Parent is VariableDeclarationSyntax
                {
                    Parent: LocalDeclarationStatementSyntax local
                } &&
                // Const locals lose constant-expression semantics when promoted
                // to ordinary fields (e.g. case labels) — exclude from allFiles.
                !local.Modifiers.Any(SyntaxKind.ConstKeyword))
            .OrderBy(declarator => declarator.SpanStart)
            .ThenBy(declarator => declarator.Span.Length)
            .ToList();

    private async Task<Solution?> TryIntroduceOneAsync(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        VariableDeclaratorSyntax declarator,
        IntroduceFieldParams bulkParams,
        CancellationToken cancellationToken)
    {
        if (IsUsingDeclaration(declarator))
            return null;

        if (declarator.Parent?.Parent is not LocalDeclarationStatementSyntax)
            return null;

        if (semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is not ILocalSymbol local)
            return null;

        // Const locals cannot keep constant-expression uses after promotion
        // (Codex P1 on PR #1308) — skip rather than emit uncompilable code.
        if (local.IsConst)
            return null;

        // Ref / ref-readonly locals cannot become ordinary fields for any
        // allFiles invocation, not only when isReadonly is set (Codex P1).
        if (local.RefKind != RefKind.None ||
            declarator.Initializer?.Value is RefExpressionSyntax)
        {
            return null;
        }

        // Method-scoped type parameters (including nested in constructed types)
        // are unavailable at field scope — skip rather than emit uncompilable
        // code under allFiles (Codex P1 on PR #1308).
        if (ContainsMethodTypeParameter(local.Type))
            return null;

        // Same for method type parameters referenced only in the initializer
        // (e.g. typeof(T) when the local is System.Type) — Codex P1 on #1316.
        if (declarator.Initializer?.Value is { } initExpr &&
            ReferencesMethodTypeParameter(initExpr, semanticModel, cancellationToken))
            return null;

        // Readonly fields cannot be mutated outside a constructor; skip locals
        // written after initialization (assignment, ++/--, ref/out) when the
        // bulk walk propagates isReadonly (Codex P1 on PR #1308).
        if (bulkParams.IsReadonly)
        {
            var writeCandidates = FindLocalReferences(root, semanticModel, local, cancellationToken)
                .Where(id => id.Span != declarator.Identifier.Span)
                .Cast<SyntaxNode>()
                .ToList();
            if (LocalIsWrittenAfterInitialization(writeCandidates))
                return null;
        }

        var fieldName = declarator.Identifier.Text;
        if (string.IsNullOrWhiteSpace(fieldName) || !IsValidIdentifier(fieldName))
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        var siteParams = new IntroduceFieldParams
        {
            SourceFile = document.FilePath,
            FieldName = fieldName,
            IsReadonly = bulkParams.IsReadonly,
            IsStatic = bulkParams.IsStatic,
            InitializeInConstructor = bulkParams.InitializeInConstructor,
            ReplaceAll = bulkParams.ReplaceAll,
            Preview = false
        };

        var span = declarator.Identifier.Span;
        var node = root.FindNode(span);
        // Ensure we stay on the local path — expression-only sites are out of
        // scope for allFiles (skip-not-throw).
        if (FindPromotableLocal(node, span, semanticModel, cancellationToken) == null)
            return null;

        var beforeText = await document.GetTextAsync(cancellationToken);
        var plan = BuildPlan(node, span, semanticModel, siteParams, cancellationToken);
        var newRoot = ApplyPlan((CompilationUnitSyntax)root, plan, siteParams);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var afterText = await newDocument.GetTextAsync(cancellationToken);
        if (beforeText.ContentEquals(afterText))
            return null;

        return newDocument.Project.Solution;
    }

    private static TextSpan GetSelectionSpan(SourceText sourceText, IntroduceFieldParams @params)
    {
        var startLineNum = @params.StartLine!.Value;
        var startColumn = @params.StartColumn!.Value;
        var endLineNum = @params.EndLine!.Value;
        var endColumn = @params.EndColumn!.Value;

        if (startLineNum > sourceText.Lines.Count || endLineNum > sourceText.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Selection is outside the file.");

        var startLine = sourceText.Lines[startLineNum - 1];
        var endLine = sourceText.Lines[endLineNum - 1];
        if (startColumn - 1 > startLine.Span.Length || endColumn - 1 > endLine.SpanIncludingLineBreak.Length)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Selection column is outside the line.");

        var startPosition = startLine.Start + startColumn - 1;
        var endPosition = endLine.Start + endColumn - 1;
        if (endPosition < startPosition)
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    private static FieldPlan BuildPlan(
        SyntaxNode node,
        TextSpan span,
        SemanticModel semanticModel,
        IntroduceFieldParams @params,
        CancellationToken cancellationToken)
    {
        var local = FindPromotableLocal(node, span, semanticModel, cancellationToken);
        ExpressionSyntax? expression = null;
        ExpressionSyntax? initializer;
        ITypeSymbol fieldType;
        IReadOnlyList<SyntaxNode> replacements;
        LocalDeclarationStatementSyntax? declarationToRemove = null;
        VariableDeclaratorSyntax? declaratorToRemove = null;

        if (local != null)
        {
            var declarator = local.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax(cancellationToken))
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Parent?.Parent is LocalDeclarationStatementSyntax);

            if (declarator == null)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    $"Local variable '{local.Name}' cannot be promoted to a field.");
            }

            RejectUsingLocal(local, declarator);

            if (local.IsConst)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    $"Local variable '{local.Name}' is const and cannot be promoted to a field.");
            }

            if (local.RefKind != RefKind.None ||
                declarator.Initializer?.Value is RefExpressionSyntax)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    $"Local variable '{local.Name}' is a ref local and cannot be promoted to a field.");
            }

            var declaration = (LocalDeclarationStatementSyntax)declarator.Parent!.Parent!;
            initializer = declarator.Initializer?.Value;
            fieldType = local.Type;
            ValidateFieldType(fieldType);

            // Initializer may reference method type parameters even when the
            // declared type does not (e.g. System.Type type = typeof(T) in M<T>).
            if (initializer != null &&
                ReferencesMethodTypeParameter(initializer, semanticModel, cancellationToken))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidTargetType,
                    "Cannot introduce a field whose initializer references a method type parameter.");
            }

            if (initializer != null)
                ValidateExpressionCaptures(initializer, semanticModel, local, @params.IsStatic, cancellationToken);

            var references = FindLocalReferences(declaration.SyntaxTree.GetRoot(), semanticModel, local, cancellationToken)
                .Where(id => id.Span != declarator.Identifier.Span)
                .Cast<SyntaxNode>()
                .ToList();

            if (@params.IsReadonly &&
                LocalIsWrittenAfterInitialization(references))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    $"Local variable '{local.Name}' is written after initialization and cannot become a readonly field.");
            }

            replacements = references;
            if (declaration.Declaration.Variables.Count == 1)
                declarationToRemove = declaration;
            else
                declaratorToRemove = declarator;
        }
        else
        {
            expression = FindEnclosingExpression(node, span);
            if (expression == null || IsTypeContext(expression))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFound,
                    "No valid expression found at the specified location.");
            }

            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            fieldType = typeInfo.ConvertedType ?? typeInfo.Type
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not determine expression type.");

            ValidateFieldType(fieldType);
            if (ReferencesMethodTypeParameter(expression, semanticModel, cancellationToken))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidTargetType,
                    "Cannot introduce a field whose initializer references a method type parameter.");
            }

            ValidateExpressionCaptures(expression, semanticModel, excludedLocal: null, @params.IsStatic, cancellationToken);

            if (ContainsAwait(expression))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    "Expression cannot be used as a field initializer.");
            }

            if (expression is AssignmentExpressionSyntax)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    "Expression cannot be used as a field initializer.");
            }

            initializer = expression;
            replacements = @params.ReplaceAll
                ? FindMatchingExpressions(GetContainingTypeOrThrow(expression), expression, semanticModel, cancellationToken)
                : new List<SyntaxNode> { expression };
        }

        var planAnchor = local?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ?? expression!;
        var containingType = GetContainingTypeOrThrow(planAnchor);
        ValidateContainingType(containingType, @params.IsStatic, semanticModel, cancellationToken);
        ValidateNameAvailable(containingType, semanticModel, @params.FieldName!, cancellationToken);
        ValidateStaticUsage(planAnchor, @params.IsStatic);

        if (!@params.InitializeInConstructor && initializer != null)
        {
            ValidateInlineInitializer(initializer, semanticModel, @params.IsStatic, cancellationToken);
            // Local promotions: instance captures are illegal in field initializers
            // (expression promotions keep prior allow-for-instance-fields behavior).
            if (local != null)
                RejectInstanceCapturesInInlineFieldInitializer(initializer, semanticModel, cancellationToken);
        }

        if (@params.InitializeInConstructor && initializer == null)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFieldInitializable,
                "Cannot initialize in a constructor without an initializer expression.");
        }

        var field = CreateFieldDeclaration(@params, fieldType, @params.InitializeInConstructor ? null : initializer);

        return new FieldPlan(
            containingType,
            containingType.Identifier.Text,
            fieldType.ToDisplayString(),
            field,
            replacements,
            declarationToRemove,
            declaratorToRemove,
            initializer,
            FindInsertBeforeFieldVariable(@params.IsStatic, planAnchor, replacements),
            replacements.Count + (declarationToRemove != null || declaratorToRemove != null ? 1 : 0));
    }

    private static CompilationUnitSyntax ApplyPlan(
        CompilationUnitSyntax root,
        FieldPlan plan,
        IntroduceFieldParams @params)
    {
        var replaceAnn = new SyntaxAnnotation("introduce-field-replace");
        var removeDeclAnn = new SyntaxAnnotation("introduce-field-remove-decl");
        var removeVarAnn = new SyntaxAnnotation("introduce-field-remove-var");
        var typeAnn = new SyntaxAnnotation("introduce-field-type");

        var annotateTargets = plan.Replacements
            .Concat(plan.DeclarationToRemove != null ? new SyntaxNode[] { plan.DeclarationToRemove } : Array.Empty<SyntaxNode>())
            .Concat(plan.DeclaratorToRemove != null ? new SyntaxNode[] { plan.DeclaratorToRemove } : Array.Empty<SyntaxNode>())
            .Distinct()
            .ToList();

        var annotated = annotateTargets.Count == 0
            ? root
            : root.ReplaceNodes(annotateTargets, (original, _) =>
            {
                var node = original;
                if (plan.Replacements.Contains(original))
                    node = node.WithAdditionalAnnotations(replaceAnn);
                if (plan.DeclarationToRemove == original)
                    node = node.WithAdditionalAnnotations(removeDeclAnn);
                if (plan.DeclaratorToRemove == original)
                    node = node.WithAdditionalAnnotations(removeVarAnn);
                return node;
            });

        var typeSeed = annotated.GetAnnotatedNodes(replaceAnn).FirstOrDefault()
            ?? annotated.GetAnnotatedNodes(removeDeclAnn).FirstOrDefault()
            ?? annotated.GetAnnotatedNodes(removeVarAnn).FirstOrDefault();
        var typeToAnnotate = typeSeed != null
            ? GetContainingTypeOrThrow(typeSeed)
            : annotated.GetCurrentNode(plan.ContainingType)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to locate containing type after rewrite.");

        annotated = (CompilationUnitSyntax)annotated.ReplaceNode(
            typeToAnnotate,
            typeToAnnotate.WithAdditionalAnnotations(typeAnn));

        var fieldRef = CreateFieldReference(@params.IsStatic, plan.ContainingTypeName, @params.FieldName!);
        var replacements = annotated.GetAnnotatedNodes(replaceAnn).ToList();
        SyntaxNode newRoot = replacements.Count == 0
            ? annotated
            : annotated.ReplaceNodes(replacements, (original, _) => fieldRef.WithTriviaFrom(original));

        var declToRemove = newRoot.GetAnnotatedNodes(removeDeclAnn).FirstOrDefault();
        if (declToRemove != null)
        {
            newRoot = newRoot.RemoveNode(declToRemove, SyntaxRemoveOptions.KeepNoTrivia)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to remove local declaration.");
        }

        var varToRemove = newRoot.GetAnnotatedNodes(removeVarAnn).FirstOrDefault();
        if (varToRemove != null)
        {
            var declaration = varToRemove.Parent as VariableDeclarationSyntax
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to update local declaration.");
            var statement = declaration.Parent as LocalDeclarationStatementSyntax
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to update local declaration.");
            var newDeclaration = declaration.RemoveNode(varToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
            newRoot = newRoot.ReplaceNode(statement, statement.WithDeclaration(newDeclaration));
        }

        var updatedType = newRoot.GetAnnotatedNodes(typeAnn).OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to locate containing type after rewrite.");
        var typeWithField = InsertField(updatedType, plan.Field, plan.InsertBeforeFieldVariable);
        if (@params.InitializeInConstructor && plan.Initializer != null)
            typeWithField = EnsureConstructorInitialization(typeWithField, @params, plan.Initializer);

        return (CompilationUnitSyntax)newRoot.ReplaceNode(updatedType, typeWithField);
    }

    private static ILocalSymbol? FindPromotableLocal(
        SyntaxNode node,
        TextSpan span,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var declarator = node.AncestorsAndSelf()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Parent?.Parent is LocalDeclarationStatementSyntax && v.Span.Contains(span));
        if (declarator != null)
        {
            var declared = semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
            if (declared != null)
                RejectUsingLocal(declared, declarator);
            return declared;
        }

        var localStatement = node.AncestorsAndSelf()
            .OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(s => s.Span.Contains(span) && s.Declaration.Variables.Count == 1);
        if (localStatement != null)
        {
            var variable = localStatement.Declaration.Variables[0];
            var declared = semanticModel.GetDeclaredSymbol(variable, cancellationToken) as ILocalSymbol;
            if (declared != null)
                RejectUsingLocal(declared, variable);
            return declared;
        }

        IdentifierNameSyntax? identifier = node as IdentifierNameSyntax
            ?? node.AncestorsAndSelf()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(id => id.Span.Contains(span));

        if (identifier == null)
            return null;

        if (semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol is ILocalSymbol local)
        {
            var syntax = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
            if (syntax is VariableDeclaratorSyntax v)
            {
                RejectUsingLocal(local, v);
                if (v.Parent?.Parent is LocalDeclarationStatementSyntax)
                    return local;
            }
        }

        return null;
    }


    private static IReadOnlyList<IdentifierNameSyntax> FindLocalReferences(
        SyntaxNode root,
        SemanticModel semanticModel,
        ILocalSymbol local,
        CancellationToken cancellationToken)
    {
        return root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id =>
            {
                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                return symbol != null && SymbolEqualityComparer.Default.Equals(symbol, local);
            })
            .ToList();
    }

    private static ExpressionSyntax? FindEnclosingExpression(SyntaxNode node, TextSpan span)
    {
        ExpressionSyntax? bestMatch = null;
        var current = node;

        while (current != null)
        {
            if (current is ExpressionSyntax expr && current.Span.Contains(span))
            {
                if (bestMatch == null || current.Span.Length <= bestMatch.Span.Length)
                    bestMatch = expr;
            }

            current = current.Parent;
        }

        return bestMatch;
    }

    private static List<SyntaxNode> FindMatchingExpressions(
        TypeDeclarationSyntax containingType,
        ExpressionSyntax original,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeExpression(original);
        var originalBindings = CollectBindings(original, semanticModel, cancellationToken);
        return containingType.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(expr =>
                expr == original ||
                (NormalizeExpression(expr) == normalized &&
                 BindingsEqual(originalBindings, CollectBindings(expr, semanticModel, cancellationToken))))
            .Cast<SyntaxNode>()
            .ToList();
    }

    private static IReadOnlyList<ISymbol?> CollectBindings(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        return expression.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(id => semanticModel.GetSymbolInfo(id, cancellationToken).Symbol)
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

    private static string NormalizeExpression(ExpressionSyntax expression) =>
        expression.NormalizeWhitespace().ToFullString().Trim();

    private static TypeDeclarationSyntax GetContainingTypeOrThrow(SyntaxNode node)
    {
        var typeDecl = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl != null)
            return typeDecl;

        if (node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce a field into this type.");
        }

        throw new RefactoringException(
            ErrorCodes.TypeNotFound,
            "Selection must be inside a type declaration.");
    }

    private static void ValidateContainingType(
        TypeDeclarationSyntax containingType,
        bool isStaticField,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (containingType is InterfaceDeclarationSyntax)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce a field into an interface.");
        }

        var symbol = semanticModel.GetDeclaredSymbol(containingType, cancellationToken) as INamedTypeSymbol;
        if (symbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve containing type.");

        if (symbol.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                $"Cannot introduce a field into '{symbol.Name}'.");
        }

        if (symbol.IsStatic && !isStaticField)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce an instance field into a static type.");
        }
    }

    private static void ValidateNameAvailable(
        TypeDeclarationSyntax containingType,
        SemanticModel semanticModel,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetDeclaredSymbol(containingType, cancellationToken) as INamedTypeSymbol;
        if (symbol?.GetMembers(fieldName).Any() == true)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Member '{fieldName}' already exists in type.");
        }

        var existingField = containingType.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.Text == fieldName);

        if (existingField != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Field '{fieldName}' already exists in type.");
        }
    }

    private static void ValidateFieldType(ITypeSymbol fieldType)
    {
        if (fieldType.SpecialType == SpecialType.System_Void)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionIsVoid,
                "Cannot introduce a field from a void expression.");
        }

        if (fieldType.TypeKind == TypeKind.Error || fieldType.IsAnonymousType)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Expression type is not a valid field type.");
        }

        if (ContainsMethodTypeParameter(fieldType))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce a field whose type uses a method type parameter.");
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> is or nests a method type parameter
    /// (arrays, pointers, and constructed type arguments). Class/type
    /// parameters remain eligible for field promotion.
    /// </summary>
    private static bool ContainsMethodTypeParameter(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol typeParameter)
            return typeParameter.TypeParameterKind == TypeParameterKind.Method;

        if (type is IArrayTypeSymbol array)
            return ContainsMethodTypeParameter(array.ElementType);

        if (type is IPointerTypeSymbol pointer)
            return ContainsMethodTypeParameter(pointer.PointedAtType);

        if (type is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (ContainsMethodTypeParameter(argument))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="node"/> references a method type parameter
    /// declared outside <paramref name="node"/> (e.g. <c>typeof(T)</c> inside
    /// <c>void M&lt;T&gt;()</c>), including nested in constructed type names.
    /// Class type parameters return false. Method type parameters declared by
    /// local functions / nested methods inside the initializer remain in scope
    /// after promotion and are ignored (Codex P2 on #1316).
    /// </summary>
    private static bool ReferencesMethodTypeParameter(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var ident in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(ident, cancellationToken).Symbol
                ?? semanticModel.GetTypeInfo(ident, cancellationToken).Type;
            if (symbol is not ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } typeParam)
                continue;

            // Nested local-function / lambda method type params stay valid in a
            // field initializer; only enclosing-method params escape scope.
            if (!IsDeclaredWithinNode(typeParam, node))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when any declaring syntax for <paramref name="symbol"/> lies inside
    /// <paramref name="node"/> (inclusive).
    /// </summary>
    private static bool IsDeclaredWithinNode(ISymbol symbol, SyntaxNode node)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var declared = reference.GetSyntax();
            if (declared == node || node.Contains(declared))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when any post-declaration use writes the local (assignment,
    /// increment/decrement, or ref/out argument / ref expression).
    /// </summary>
    private static bool LocalIsWrittenAfterInitialization(IReadOnlyList<SyntaxNode> references)
    {
        foreach (var reference in references)
        {
            if (reference is not IdentifierNameSyntax identifier)
                continue;

            if (IsWriteUsage(identifier))
                return true;
        }

        return false;
    }

    private static bool IsWriteUsage(IdentifierNameSyntax identifier)
    {
        // Cover simple and deconstruction lefts: (x, y) = ... still writes x.
        foreach (var assignment in identifier.Ancestors().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left.DescendantNodesAndSelf().Contains(identifier))
                return true;
        }

        if (identifier.Parent is PrefixUnaryExpressionSyntax prefix &&
            (prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
             prefix.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return true;
        }

        if (identifier.Parent is PostfixUnaryExpressionSyntax postfix &&
            (postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
             postfix.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            return true;
        }

        if (identifier.Parent is ArgumentSyntax arg &&
            (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
             arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return true;
        }

        if (identifier.Parent is RefExpressionSyntax)
            return true;

        return false;
    }

    private static void ValidateExpressionCaptures(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        ILocalSymbol? excludedLocal,
        bool isStaticField,
        CancellationToken cancellationToken)
    {
        foreach (var ident in expression.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(ident, cancellationToken).Symbol;
            if (symbol == null)
                continue;

            if (symbol is ILocalSymbol local &&
                (excludedLocal == null || !SymbolEqualityComparer.Default.Equals(local, excludedLocal)))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionCapturesLocal,
                    $"Expression captures local variable '{local.Name}'.");
            }

            if (symbol is IParameterSymbol parameter && parameter.ContainingSymbol is IMethodSymbol)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionCapturesLocal,
                    $"Expression captures parameter '{parameter.Name}'.");
            }

            // Local functions declared outside the initializer disappear when
            // the expression moves to type scope, even when static. Local
            // functions nested inside the initializer move with it and remain
            // valid (Codex P1 on #1316).
            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } localFunction &&
                !IsDeclaredWithinNode(localFunction, expression))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionCapturesLocal,
                    $"Expression captures local function '{localFunction.Name}'.");
            }

            // Static field initializers may call instance members on an explicit
            // other-object receiver (e.g. new Helper().Get<int>()). Only reject
            // containing-instance access (Codex P2 on #1316).
            if (isStaticField &&
                symbol is ISymbol { IsStatic: false, Kind: not Microsoft.CodeAnalysis.SymbolKind.Namespace and not Microsoft.CodeAnalysis.SymbolKind.NamedType } &&
                IsAccessedViaContainingInstance(ident, expression, semanticModel, cancellationToken))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    "A static field initializer cannot reference instance members.");
            }
        }

        if (isStaticField && expression.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFieldInitializable,
                "A static field initializer cannot reference instance members.");
        }
    }

    private static void ValidateInlineInitializer(
        ExpressionSyntax initializer,
        SemanticModel semanticModel,
        bool isStaticField,
        CancellationToken cancellationToken)
    {
        if (ContainsAwait(initializer))
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFieldInitializable,
                "Expression cannot be used as a field initializer.");
        }

        ValidateExpressionCaptures(initializer, semanticModel, excludedLocal: null, isStaticField, cancellationToken);
    }

    /// <summary>
    /// Field initializers cannot reference <c>this</c>/<c>base</c> or instance
    /// members of the containing instance. Used for local promotions when not
    /// initializing in a constructor (Codex P1 on PR #1308; tightened on #1316).
    /// </summary>
    private static void RejectInstanceCapturesInInlineFieldInitializer(
        ExpressionSyntax initializer,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        // Include GenericNameSyntax (e.g. Get<int>()) — IdentifierNameSyntax alone misses those.
        foreach (var name in initializer.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol;
            if (symbol == null)
                continue;

            if (symbol is ILocalSymbol or IParameterSymbol)
                continue;

            // A local function declared inside the initializer moves with it;
            // it is not a containing-instance member even when non-static.
            // Enclosing local functions are rejected by ValidateExpressionCaptures.
            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } localFunction &&
                IsDeclaredWithinNode(localFunction, initializer))
            {
                continue;
            }

            // Limit to symbols that can actually represent instance members.
            // Type parameters (typeof(T)), query range variables, etc. are
            // IsStatic==false but are not containing-instance captures (Codex P2).
            if (!CouldBeContainingInstanceMember(symbol))
                continue;

            // Only reject members accessed via implicit this, this, or base —
            // not instance members of other objects (e.g. DateTime.Now.Day).
            if (IsAccessedViaContainingInstance(name, initializer, semanticModel, cancellationToken))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    "A field initializer cannot reference instance members.");
            }
        }

        // Explicit this/base are illegal in field initializers even when nested
        // in nameof(...) (nameof(this.Value) / nameof(base.Value)). Bare
        // nameof(Value) remains allowed via the name-scan nameof exemption
        // (Codex P1 on #1316).
        if (initializer.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any() ||
            initializer.DescendantNodesAndSelf().OfType<BaseExpressionSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFieldInitializable,
                "A field initializer cannot reference instance members.");
        }
    }

    /// <summary>
    /// True for non-static method / property / field / event symbols (including
    /// local functions). Excludes type parameters, range variables, namespaces,
    /// and named types that are not containing-instance members.
    /// </summary>
    private static bool CouldBeContainingInstanceMember(ISymbol symbol)
    {
        if (symbol.IsStatic)
            return false;

        return symbol.Kind is Microsoft.CodeAnalysis.SymbolKind.Method
            or Microsoft.CodeAnalysis.SymbolKind.Property
            or Microsoft.CodeAnalysis.SymbolKind.Field
            or Microsoft.CodeAnalysis.SymbolKind.Event;
    }

    /// <summary>
    /// True when <paramref name="name"/> refers to a member of the containing
    /// instance (implicit receiver, <c>this</c>, or <c>base</c>), rather than
    /// an explicit other-object receiver, object/with-initializer or property-pattern
    /// member designator, or nameof argument.
    /// </summary>
    private static bool IsAccessedViaContainingInstance(
        SimpleNameSyntax name,
        SyntaxNode movedSubtree,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        // nameof(...) is unevaluated / compile-time — never captures the instance.
        // Uses Identifier.Text (not ValueText) so escaped @nameof(...) method
        // calls are not treated as the nameof operator (Codex P2).
        if (IsInsideNameofArgument(name))
            return false;

        // Object/with-initializer and property-pattern designators first —
        // extended patterns like is { Child.Value: 1 } are MemberAccess shaped
        // but are not containing-instance reads (Codex P2).
        if (IsObjectOrWithInitializerMemberDesignator(name))
            return false;

        if (IsPropertyPatternMemberDesignator(name))
            return false;

        if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
        {
            return IsContainingInstanceReceiver(memberAccess.Expression, movedSubtree, semanticModel, cancellationToken);
        }

        if (name.Parent is MemberBindingExpressionSyntax)
        {
            var conditional = name.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();
            return conditional != null &&
                IsContainingInstanceReceiver(conditional.Expression, movedSubtree, semanticModel, cancellationToken);
        }

        // nameof(...) references are compile-time only and do not capture instance state.
        if (MethodSymbolHelpers.IsInNameof(name))
            return false;

        // Bare simple name / invocation target → implicit this (or static, already filtered).
        // Includes inaccessible local functions referenced by bare name.
        return true;
    }

    /// <summary>
    /// True when <paramref name="node"/> occurs inside a real <c>nameof(...)</c>
    /// operator argument. Uses <see cref="SyntaxToken.Text"/> so an escaped
    /// <c>@nameof(...)</c> method call is not treated as unevaluated.
    /// </summary>
    private static bool IsInsideNameofArgument(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            // Text == "nameof" excludes verbatim @nameof (ValueText is still "nameof").
            if (ancestor is InvocationExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.Text: "nameof" }
                })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="name"/> is the left-hand designator of an
    /// assignment inside an object or with initializer expression.
    /// </summary>
    private static bool IsObjectOrWithInitializerMemberDesignator(SimpleNameSyntax name)
    {
        if (name.Parent is not AssignmentExpressionSyntax assignment || assignment.Left != name)
            return false;

        return assignment.Parent is InitializerExpressionSyntax initializer &&
               (initializer.IsKind(SyntaxKind.ObjectInitializerExpression) ||
                initializer.IsKind(SyntaxKind.WithInitializerExpression));
    }

    /// <summary>
    /// True when <paramref name="name"/> is a property (or named) subpattern
    /// designator, e.g. <c>Value</c> in <c>is { Value: 1 }</c>.
    /// </summary>
    private static bool IsPropertyPatternMemberDesignator(SimpleNameSyntax name)
    {
        if (name.Parent is NameColonSyntax { Parent: SubpatternSyntax })
            return true;

        // Extended property patterns (e.g. is { Child.Value: 1 }) use
        // ExpressionColonSyntax with a member-access designator (Codex P2).
        // Only the left-hand designator is exempt — not names inside the nested pattern.
        foreach (var colon in name.Ancestors().OfType<ExpressionColonSyntax>())
        {
            if (colon.Parent is not SubpatternSyntax)
                continue;

            if (colon.Expression.Span.Contains(name.Span))
                return true;
        }

        return false;
    }

    private static bool IsContainingInstanceReceiver(
        ExpressionSyntax expression,
        SyntaxNode movedSubtree,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        if (expression is ThisExpressionSyntax or BaseExpressionSyntax)
            return true;

        return expression switch
        {
            SimpleNameSyntax simpleName => IsImplicitContainingInstanceMember(simpleName, movedSubtree, semanticModel, cancellationToken),
            MemberAccessExpressionSyntax memberAccess => IsContainingInstanceReceiver(memberAccess.Expression, movedSubtree, semanticModel, cancellationToken),
            InvocationExpressionSyntax invocation => IsContainingInstanceReceiver(invocation.Expression, movedSubtree, semanticModel, cancellationToken),
            ElementAccessExpressionSyntax elementAccess => IsContainingInstanceReceiver(elementAccess.Expression, movedSubtree, semanticModel, cancellationToken),
            ConditionalAccessExpressionSyntax conditionalAccess => IsContainingInstanceReceiver(conditionalAccess.Expression, movedSubtree, semanticModel, cancellationToken),
            _ => expression.DescendantNodesAndSelf().Any(node =>
                node is ThisExpressionSyntax or BaseExpressionSyntax)
        };
    }

    private static bool IsImplicitContainingInstanceMember(
        SimpleNameSyntax simpleName,
        SyntaxNode movedSubtree,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(simpleName, cancellationToken).Symbol;
        if (symbol == null)
            return false;

        if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
            return false;

        // Local functions declared inside the moved initializer are not
        // containing-instance receivers (Codex P2 on #1316).
        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } localFunction &&
            IsDeclaredWithinNode(localFunction, movedSubtree))
        {
            return false;
        }

        return CouldBeContainingInstanceMember(symbol);
    }

    private static void ValidateStaticUsage(SyntaxNode node, bool isStaticField)
    {
        if (isStaticField)
            return;

        if (IsInStaticMember(node))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce an instance field from a static member.");
        }
    }

    private static bool IsInStaticMember(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Modifiers.Any(SyntaxKind.StaticKeyword);
                case PropertyDeclarationSyntax property:
                    return property.Modifiers.Any(SyntaxKind.StaticKeyword);
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Modifiers.Any(SyntaxKind.StaticKeyword);
                case LocalFunctionStatementSyntax localFunction:
                    return localFunction.Modifiers.Any(SyntaxKind.StaticKeyword);
                case FieldDeclarationSyntax field:
                    return field.Modifiers.Any(SyntaxKind.StaticKeyword);
                case VariableDeclarationSyntax:
                    continue;
            }
        }

        return false;
    }

    private static bool ContainsAwait(SyntaxNode node) =>
        node.DescendantNodesAndSelf().Any(n => n.IsKind(SyntaxKind.AwaitExpression));

    private static bool IsTypeContext(ExpressionSyntax expression)
    {
        return expression.Parent switch
        {
            VariableDeclarationSyntax vd when vd.Type == expression => true,
            ParameterSyntax p when p.Type == expression => true,
            MethodDeclarationSyntax m when m.ReturnType == expression => true,
            PropertyDeclarationSyntax p when p.Type == expression => true,
            TypeArgumentListSyntax => true,
            QualifiedNameSyntax => true,
            AliasQualifiedNameSyntax => true,
            CastExpressionSyntax c when c.Type == expression => true,
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AsExpression) && b.Right == expression => true,
            TypeConstraintSyntax => true,
            BaseListSyntax => true,
            SimpleBaseTypeSyntax => true,
            ArrayTypeSyntax => true,
            NullableTypeSyntax => true,
            ForEachStatementSyntax f when f.Type == expression => true,
            _ => false
        };
    }

    private static FieldDeclarationSyntax CreateFieldDeclaration(
        IntroduceFieldParams @params,
        ITypeSymbol type,
        ExpressionSyntax? initializer)
    {
        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space)
        };

        if (@params.IsStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        if (@params.IsReadonly)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var declarator = SyntaxFactory.VariableDeclarator(@params.FieldName!);
        if (initializer != null)
        {
            declarator = declarator.WithInitializer(
                SyntaxFactory.EqualsValueClause(initializer.WithoutTrivia()));
        }

        return SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName(type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .NormalizeWhitespace();
    }

    private static TypeDeclarationSyntax InsertField(
        TypeDeclarationSyntax typeDeclaration,
        FieldDeclarationSyntax field,
        string? insertBeforeFieldVariable)
    {
        var members = typeDeclaration.Members.ToList();
        var insertIndex = FindFieldInsertIndex(members, insertBeforeFieldVariable);

        members.Insert(insertIndex, field
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    private static int FindFieldInsertIndex(IReadOnlyList<MemberDeclarationSyntax> members, string? insertBeforeFieldVariable)
    {
        if (insertBeforeFieldVariable != null)
        {
            var sourceIndex = -1;
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i] is FieldDeclarationSyntax existing &&
                    existing.Declaration.Variables.Any(v => v.Identifier.Text == insertBeforeFieldVariable))
                {
                    sourceIndex = i;
                    break;
                }
            }

            if (sourceIndex >= 0)
                return sourceIndex;
        }

        var insertIndex = 0;
        for (var i = 0; i < members.Count; i++)
        {
            if (members[i] is FieldDeclarationSyntax)
                insertIndex = i + 1;
            else if (insertIndex > 0)
                break;
        }

        return insertIndex;
    }

    private static TypeDeclarationSyntax EnsureConstructorInitialization(
        TypeDeclarationSyntax typeDeclaration,
        IntroduceFieldParams @params,
        ExpressionSyntax initializer)
    {
        if (!@params.IsStatic && typeDeclaration.ParameterList != null)
        {
            var hasInstanceCtor = typeDeclaration.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Any(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword));
            if (!hasInstanceCtor)
            {
                throw new RefactoringException(
                    ErrorCodes.ConstructorNotFound,
                    "Cannot initialize in a constructor on a type that only has a primary constructor.");
            }
        }

        var assignment = CreateAssignment(
            CreateFieldReference(@params.IsStatic, typeDeclaration.Identifier.Text, @params.FieldName!),
            initializer);
        var constructors = typeDeclaration.Members
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => @params.IsStatic
                ? c.Modifiers.Any(SyntaxKind.StaticKeyword)
                : !c.Modifiers.Any(SyntaxKind.StaticKeyword) && !ChainsToThis(c))
            .ToList();

        if (constructors.Count == 0)
        {
            var created = CreateConstructor(typeDeclaration.Identifier.Text, @params.IsStatic, assignment);
            var members = typeDeclaration.Members.ToList();
            var insertIndex = 0;
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i] is FieldDeclarationSyntax)
                    insertIndex = i + 1;
            }

            members.Insert(insertIndex, created
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            return typeDeclaration.WithMembers(SyntaxFactory.List(members));
        }

        return typeDeclaration.ReplaceNodes(constructors, (original, _) => AddAssignmentToConstructor(original, assignment));
    }

    private static bool ChainsToThis(ConstructorDeclarationSyntax constructor) =>
        constructor.Initializer?.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) == true;

    private static ConstructorDeclarationSyntax AddAssignmentToConstructor(
        ConstructorDeclarationSyntax constructor,
        ExpressionStatementSyntax assignment)
    {
        if (constructor.ExpressionBody != null)
        {
            var original = SyntaxFactory.ExpressionStatement(constructor.ExpressionBody.Expression);
            var body = SyntaxFactory.Block(assignment, original);
            return constructor
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(body.NormalizeWhitespace());
        }

        var bodyBlock = constructor.Body ?? SyntaxFactory.Block();
        return constructor.WithBody(
            bodyBlock.WithStatements(bodyBlock.Statements.Insert(0, assignment)));
    }

    private static ConstructorDeclarationSyntax CreateConstructor(
        string typeName,
        bool isStatic,
        ExpressionStatementSyntax assignment)
    {
        var modifiers = isStatic
            ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        return SyntaxFactory.ConstructorDeclaration(typeName)
            .WithModifiers(modifiers)
            .WithParameterList(SyntaxFactory.ParameterList())
            .WithBody(SyntaxFactory.Block(assignment))
            .NormalizeWhitespace();
    }

    private static ExpressionStatementSyntax CreateAssignment(ExpressionSyntax fieldRef, ExpressionSyntax initializer) =>
        SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    fieldRef,
                    initializer.WithoutTrivia()))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

    private static ExpressionSyntax CreateFieldReference(bool isStatic, string typeName, string fieldName)
    {
        var name = SyntaxFactory.IdentifierName(fieldName);
        if (isStatic)
        {
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(typeName),
                name);
        }

        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            name);
    }

    private static void RejectUsingLocal(ILocalSymbol local, VariableDeclaratorSyntax declarator)
    {
        if (!IsUsingDeclaration(declarator))
            return;

        throw new RefactoringException(
            ErrorCodes.ExpressionNotFieldInitializable,
            $"Local variable '{local.Name}' is a using declaration and cannot be promoted to a field.");
    }

    private static bool IsUsingDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent switch
        {
            LocalDeclarationStatementSyntax statement => statement.UsingKeyword != default,
            UsingStatementSyntax => true,
            _ => false
        };

    private static string? FindInsertBeforeFieldVariable(
        bool isStaticField,
        SyntaxNode planAnchor,
        IReadOnlyList<SyntaxNode> replacements)
    {
        if (!isStaticField)
            return null;

        var sourceField = planAnchor.AncestorsAndSelf()
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Modifiers.Any(SyntaxKind.StaticKeyword));

        sourceField ??= replacements
            .Select(r => r.Ancestors()
                .OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f => f.Modifiers.Any(SyntaxKind.StaticKeyword)))
            .Where(f => f != null)
            .OrderBy(f => f!.SpanStart)
            .FirstOrDefault();

        return sourceField?.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string sourceFile,
        string fieldName,
        IntroduceFieldParams @params,
        FieldPlan plan)
    {
        var initNote = @params.InitializeInConstructor
            ? " initialized in constructor"
            : string.Empty;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = sourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Introduce field '{fieldName}' of type {plan.FieldType}{initNote}",
                BeforeSnippet = "// (selected expression or local)",
                AfterSnippet = plan.Field.NormalizeWhitespace().ToFullString()
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (!SyntaxFacts.IsValidIdentifier(name))
            return false;
        return !SyntaxFacts.IsKeywordKind(SyntaxFacts.GetKeywordKind(name));
    }

    private sealed record FieldPlan(
        TypeDeclarationSyntax ContainingType,
        string ContainingTypeName,
        string FieldType,
        FieldDeclarationSyntax Field,
        IReadOnlyList<SyntaxNode> Replacements,
        LocalDeclarationStatementSyntax? DeclarationToRemove,
        VariableDeclaratorSyntax? DeclaratorToRemove,
        ExpressionSyntax? Initializer,
        string? InsertBeforeFieldVariable,
        int ReplacementCount);
}

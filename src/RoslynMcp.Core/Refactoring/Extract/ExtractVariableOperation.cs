using System.Text;
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
/// Extracts an expression to a local variable.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and extracts every eligible outermost non-trivial
/// expression, naming each variable from that expression and skipping
/// ineligible sites rather than throwing.
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
    protected override void ValidateParams(ExtractVariableParams @params) => Validate(@params);

    /// <summary>
    /// Validates <paramref name="params"/> the same way
    /// <see cref="ValidateParams"/> does (exposed for unit tests).
    /// </summary>
    internal static void Validate(ExtractVariableParams @params)
    {
        if (@params.AllFiles)
        {
            if (@params.StartLine.HasValue ||
                @params.StartColumn.HasValue ||
                @params.EndLine.HasValue ||
                @params.EndColumn.HasValue ||
                @params.VariableName is not null)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with startLine, startColumn, endLine, endColumn, or variableName.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            {
                ValidateSourceFilePath(@params.SourceFile!);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!@params.StartLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startLine is required.");

        if (!@params.StartColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startColumn is required.");

        if (!@params.EndLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endLine is required.");

        if (!@params.EndColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endColumn is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.StartLine.Value < 1 || @params.EndLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line numbers must be >= 1.");

        if (@params.StartColumn.Value < 1 || @params.EndColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column numbers must be >= 1.");

        if (@params.StartLine.Value > @params.EndLine.Value ||
            (@params.StartLine.Value == @params.EndLine.Value &&
             @params.StartColumn.Value >= @params.EndColumn.Value))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "Selection start must be before end.");

        if (!IdentifierValidation.IsValidIdentifier(@params.VariableName!))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid variable name: {@params.VariableName}");
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
        ExtractVariableParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var variableName = @params.VariableName!;
        var startLine = @params.StartLine!.Value;
        var startColumn = @params.StartColumn!.Value;
        var endLine = @params.EndLine!.Value;
        var endColumn = @params.EndColumn!.Value;

        var document = GetDocumentOrThrow(sourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Get text span from line/column (bounds-checked like extract_method / extract_constant)
        var sourceText = await document.GetTextAsync(cancellationToken);
        var startPosition = SymbolResolver.GetPosition(sourceText, startLine, startColumn);
        var endPosition = SymbolResolver.GetPosition(sourceText, endLine, endColumn);
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

        var extracted = TryBuildExtractedSolution(
            document,
            root,
            semanticModel,
            expression,
            variableName,
            @params.UseVar,
            @params.ReplaceAll,
            cancellationToken,
            out var typeInfoType,
            out var variableDeclaration,
            out var replacementCount);

        if (extracted == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                "Could not extract expression to variable.");
        }

        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                sourceFile,
                variableName,
                expression,
                typeInfoType!,
                variableDeclaration!,
                replacementCount);
        }

        var commitResult = await CommitChangesAsync(extracted, cancellationToken);

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
            0,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>ExtractConstantOperation.ExecuteAllFilesAsync</c>
    /// / <c>IntroduceFieldOperation.ExecuteAllFilesAsync</c>) and extracts
    /// every eligible outermost non-trivial expression to a local variable
    /// named from the expression text. Optional <c>sourceFile</c> limits via
    /// <see cref="DocumentSourceFileFilter"/>. Linked documents that share a
    /// physical path are rewritten once and the same text is applied to every
    /// sibling <see cref="DocumentId"/> via
    /// <see cref="PathResolver.GetPathComparisonKey"/>. Uneditable /
    /// source-generated docs, name collisions, void / ineligible expressions,
    /// empty/invalid derived names, side-effecting sites when
    /// <c>replaceAll</c> is set, and otherwise ineligible targets are skipped
    /// rather than failing the walk. Deterministic <c>SpanStart</c> order
    /// within a file. When every file is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ExtractVariableParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var extractedCountByDoc = new Dictionary<DocumentId, int>();

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
                ExpressionSyntax? updatedExpression = null;
                foreach (var expression in CollectEligibleExpressions(root, semanticModel, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        updated = TryExtractOne(
                            currentDocument,
                            root,
                            semanticModel,
                            expression,
                            @params,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        updated = null;
                    }

                    if (updated != null &&
                        !await LinkedViewsCanHonorRewriteAsync(
                            linkedDocuments,
                            currentDocument,
                            expression,
                            @params,
                            currentSolution,
                            updated,
                            cancellationToken))
                    {
                        updated = null;
                        continue;
                    }

                    if (updated != null)
                    {
                        updatedExpression = expression;
                        break;
                    }
                }

                if (updated == null)
                    break;

                _ = updatedExpression;

                var beforeSolution = currentSolution;
                currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                    beforeSolution,
                    updated,
                    Context.Workspace,
                    cancellationToken);

                extractedCountByDoc[primary.Id] =
                    extractedCountByDoc.GetValueOrDefault(primary.Id) + 1;
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
                var extractedCount = extractedCountByDoc.GetValueOrDefault(document.Id);
                if (extractedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        extractedCount = Math.Max(extractedCount, extractedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = extractedCount > 0
                        ? BuildAllFilesDescription(extractedCount)
                        : "Update extract_variable rewrites",
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
            _ => matchedDocuments
        };
    }

    /// <summary>
    /// Preview description for a file that extracted
    /// <paramref name="extractedCount"/> variables.
    /// </summary>
    internal static string BuildAllFilesDescription(int extractedCount) =>
        extractedCount == 1
            ? "Extract variable"
            : $"Extract {extractedCount} variables";

    /// <summary>
    /// Collects every outermost eligible non-trivial expression in
    /// <paramref name="root"/> that lives inside a method/accessor/local-function
    /// block. Deterministic <c>SpanStart</c> then span-length order.
    /// </summary>
    internal static IReadOnlyList<ExpressionSyntax> CollectEligibleExpressions(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var kindCandidates = root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(expr => expr is not ParenthesizedExpressionSyntax)
            .Where(IsEligibleExpressionKind)
            .Distinct()
            .ToList();

        var kindSet = new HashSet<ExpressionSyntax>(kindCandidates);

        return kindCandidates
            .Where(expr => !HasEligibleAncestor(expr, kindSet))
            .Where(expr => IsStructurallyEligible(expr))
            .Where(expr => IsSemanticallyEligible(expr, semanticModel, cancellationToken))
            .OrderBy(expr => expr.SpanStart)
            .ThenBy(expr => expr.Span.Length)
            .ToList();
    }

    /// <summary>
    /// True when an ancestor expression (skipping parentheses) is itself an
    /// eligible kind candidate — used to keep only outermost sites.
    /// </summary>
    private static bool HasEligibleAncestor(ExpressionSyntax expression, HashSet<ExpressionSyntax> kindSet)
    {
        foreach (var ancestor in expression.Ancestors().OfType<ExpressionSyntax>())
        {
            if (ancestor is ParenthesizedExpressionSyntax)
                continue;

            if (kindSet.Contains(ancestor))
                return true;
        }

        return false;
    }

    private static bool IsEligibleExpressionKind(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case InvocationExpressionSyntax invocation:
                return !IsNameofInvocation(invocation);
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
            case ConditionalExpressionSyntax:
            case ElementAccessExpressionSyntax:
            case AwaitExpressionSyntax:
            case CastExpressionSyntax:
            case InterpolatedStringExpressionSyntax:
            case SwitchExpressionSyntax:
                return true;
            case IsPatternExpressionSyntax pattern:
                // Declaration patterns introduce scoped variables that must not
                // be hoisted (Copilot P1).
                return !pattern.Pattern.DescendantNodesAndSelf()
                    .OfType<VariableDesignationSyntax>()
                    .Any();
            case BinaryExpressionSyntax binary:
                return !binary.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.AddAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.SubtractAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.MultiplyAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.DivideAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.ModuloAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.AndAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.OrAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.LeftShiftAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.RightShiftAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.CoalesceAssignmentExpression) &&
                       !binary.IsKind(SyntaxKind.UnsignedRightShiftAssignmentExpression);
            default:
                return false;
        }
    }

    private static bool IsNameofInvocation(InvocationExpressionSyntax invocation) =>
        invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" };

    private static bool IsStructurallyEligible(ExpressionSyntax expression)
    {
        if (expression.Ancestors().OfType<AttributeArgumentSyntax>().Any())
            return false;

        if (GetInnermostBlock(expression) == null)
            return false;

        if (expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault() == null)
            return false;

        // Whole-statement expressions (e.g. Foo();) are not useful extract targets.
        if (expression.Parent is ExpressionStatementSyntax)
            return false;

        // Already the initializer of a local — already "extracted".
        if (expression.Parent is EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax
                {
                    Parent: VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax }
                }
            })
        {
            return false;
        }

        // Skip sites whose evaluation scope is not the nearest block statement
        // (loop condition/increment, expression-bodied lambda/local function,
        // query clauses) — hoisting would change semantics (Codex/Copilot P1).
        if (CrossesExecutionBoundary(expression))
            return false;

        // Element / member access used as assignment / increment / ref target
        // must not be extracted as a value (Codex P1).
        if (IsWriteTarget(expression))
            return false;

        return true;
    }

    private static bool CrossesExecutionBoundary(ExpressionSyntax expression)
    {
        // Expression-bodied lambda / local function / anonymous method.
        if (expression.Ancestors().Any(a =>
                a is SimpleLambdaExpressionSyntax or
                    ParenthesizedLambdaExpressionSyntax or
                    AnonymousMethodExpressionSyntax or
                    QueryClauseSyntax or
                    QueryBodySyntax))
        {
            return true;
        }

        // Expression-bodied member (=>) without a block.
        if (expression.Ancestors().OfType<ArrowExpressionClauseSyntax>().Any())
            return true;

        // Loop / if / switch conditions and for-incrementors evaluate in a
        // different control-flow site than the nearest block body.
        foreach (var ancestor in expression.Ancestors())
        {
            switch (ancestor)
            {
                case WhileStatementSyntax whileStmt
                    when whileStmt.Condition == expression || whileStmt.Condition.Contains(expression):
                case DoStatementSyntax doStmt
                    when doStmt.Condition == expression || doStmt.Condition.Contains(expression):
                case IfStatementSyntax ifStmt
                    when ifStmt.Condition == expression || ifStmt.Condition.Contains(expression):
                case ForStatementSyntax forStmt
                    when (forStmt.Condition != null &&
                          (forStmt.Condition == expression || forStmt.Condition.Contains(expression))) ||
                         forStmt.Incrementors.Any(inc => inc == expression || inc.Contains(expression)) ||
                         forStmt.Initializers.Any(init => init == expression || init.Contains(expression)):
                case ForEachStatementSyntax forEach
                    when forEach.Expression == expression || forEach.Expression.Contains(expression):
                case ForEachVariableStatementSyntax forEachVar
                    when forEachVar.Expression == expression || forEachVar.Expression.Contains(expression):
                case SwitchStatementSyntax switchStmt
                    when switchStmt.Expression == expression || switchStmt.Expression.Contains(expression):
                case LockStatementSyntax lockStmt
                    when lockStmt.Expression == expression || lockStmt.Expression.Contains(expression):
                case UsingStatementSyntax usingStmt
                    when usingStmt.Expression != null &&
                         (usingStmt.Expression == expression || usingStmt.Expression.Contains(expression)):
                    return true;
            }
        }

        return false;
    }

    private static bool IsWriteTarget(ExpressionSyntax expression)
    {
        var current = (SyntaxNode)expression;
        while (current.Parent is ParenthesizedExpressionSyntax paren)
            current = paren;

        return current.Parent switch
        {
            AssignmentExpressionSyntax assignment when assignment.Left == current => true,
            PrefixUnaryExpressionSyntax prefix
                when (prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                      prefix.IsKind(SyntaxKind.PreDecrementExpression)) &&
                     prefix.Operand == current => true,
            PostfixUnaryExpressionSyntax postfix
                when (postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                      postfix.IsKind(SyntaxKind.PostDecrementExpression)) &&
                     postfix.Operand == current => true,
            ArgumentSyntax argument
                when argument.Expression == current &&
                     (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
                      argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                      argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword)) => true,
            RefExpressionSyntax => true,
            _ => false
        };
    }

    private static bool IsSemanticallyEligible(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
        if (typeInfo.Type == null)
            return false;

        if (typeInfo.Type.SpecialType == SpecialType.System_Void)
            return false;

        return true;
    }

    private Solution? TryExtractOne(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        ExtractVariableParams bulkParams,
        CancellationToken cancellationToken)
    {
        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        var variableName = DeriveVariableNameFromExpression(expression);
        if (variableName == null)
            return null;

        return TryBuildExtractedSolution(
            document,
            root,
            semanticModel,
            expression,
            variableName,
            bulkParams.UseVar,
            bulkParams.ReplaceAll,
            cancellationToken,
            out _,
            out _,
            out _);
    }

    private static Solution? TryBuildExtractedSolution(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        string variableName,
        bool useVar,
        bool replaceAll,
        CancellationToken cancellationToken,
        out ITypeSymbol? typeInfoType,
        out LocalDeclarationStatementSyntax? variableDeclaration,
        out int replacementCount)
    {
        typeInfoType = null;
        variableDeclaration = null;
        replacementCount = 0;

        var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
        if (typeInfo.Type == null)
            return null;

        if (typeInfo.Type.SpecialType == SpecialType.System_Void)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionIsVoid,
                "Cannot extract void expression to variable.");
        }

        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            throw new RefactoringException(
                ErrorCodes.StatementNotFound,
                "Expression must be inside a statement.");
        }

        var containingBlock = containingStatement.Parent as BlockSyntax;
        if (NameCollidesInScope(semanticModel, expression, variableName, cancellationToken))
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Variable '{variableName}' already exists in scope.");
        }

        if (replaceAll && HasSideEffects(expression, semanticModel, cancellationToken))
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionHasSideEffects,
                "Cannot replace all occurrences of an expression with side effects.");
        }

        var replacements = replaceAll
            ? FindEquivalentExpressions(expression, semanticModel, cancellationToken)
            : new List<ExpressionSyntax> { expression };

        // Implicit `new()` cannot be the initializer of a `var` local (no
        // target type). Prefer the explicit converted/type display, or skip
        // when the type cannot be spelled (Copilot P1).
        TypeSyntax typeSyntax;
        var isImplicitCreation = expression is ImplicitObjectCreationExpressionSyntax ||
                                 Unwrap(expression) is ImplicitObjectCreationExpressionSyntax;
        if (isImplicitCreation)
        {
            var explicitType = typeInfo.ConvertedType ?? typeInfo.Type;
            if (explicitType == null || explicitType.IsAnonymousType)
                return null;

            typeSyntax = SyntaxFactory.ParseTypeName(explicitType.ToDisplayString());
        }
        else if (useVar || typeInfo.Type.IsAnonymousType)
        {
            typeSyntax = SyntaxFactory.IdentifierName("var");
        }
        else
        {
            typeSyntax = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
        }

        variableDeclaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(typeSyntax.WithTrailingTrivia(SyntaxFactory.Space))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(variableName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(expression)))));

        SyntaxNode newRoot;
        if (replacements.Count <= 1)
        {
            newRoot = ApplySingleReplacement(root, expression, containingStatement, containingBlock, variableName, variableDeclaration);
        }
        else
        {
            newRoot = ApplyReplaceAll(root, replacements, variableName, variableDeclaration);
        }

        typeInfoType = typeInfo.Type;
        replacementCount = replacements.Count;
        return document.WithSyntaxRoot(newRoot).Project.Solution;
    }

    /// <summary>
    /// Derives a camelCase-ish valid identifier from an expression.
    /// Prefers invoked simple name / created type name / sanitized expression text.
    /// Returns <see langword="null"/> when the name would be empty, invalid, or
    /// an unfixable keyword collision.
    /// </summary>
    internal static string? DeriveVariableNameFromExpression(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);

        string? seed = expression switch
        {
            InvocationExpressionSyntax invocation => PreferInvokedName(invocation),
            ObjectCreationExpressionSyntax creation => PreferTypeName(creation.Type),
            ImplicitObjectCreationExpressionSyntax => PreferTypeNameFromSemanticFallback(expression),
            ElementAccessExpressionSyntax access => PreferInvokedNameFromExpression(access.Expression),
            AwaitExpressionSyntax awaitExpr => PreferInvokedNameFromExpression(awaitExpr.Expression)
                ?? PreferTypeNameFromSemanticFallback(awaitExpr.Expression),
            CastExpressionSyntax cast => PreferTypeName(cast.Type),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) =>
                PreferTypeName(binary.Right as TypeSyntax),
            _ => null
        };

        // Fall back to sanitized expression text (documented allFiles contract)
        // rather than fixed labels that collide across unrelated sites (Copilot).
        seed ??= SanitizeIdentifierSeed(expression.ToString());

        return FinalizeVariableName(seed);
    }

    private static string? PreferTypeNameFromSemanticFallback(ExpressionSyntax expression) =>
        SanitizeIdentifierSeed(expression.ToString());

    /// <summary>
    /// True when <paramref name="variableName"/> already binds in scope at the
    /// expression (locals, parameters, range variables, local functions) —
    /// bulk cannot pick a safer name so these sites are skipped (Codex/Copilot P1).
    /// </summary>
    private static bool NameCollidesInScope(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        string variableName,
        CancellationToken cancellationToken)
    {
        var bareName = SyntaxIdentifierValidation.NormalizeIdentifier(variableName);
        var symbols = semanticModel.LookupSymbols(expression.SpanStart, name: bareName);
        foreach (var symbol in symbols)
        {
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or IMethodSymbol
                {
                    MethodKind: MethodKind.LocalFunction
                })
            {
                return true;
            }
        }

        return false;
    }

    private static string? PreferInvokedName(InvocationExpressionSyntax invocation) =>
        PreferInvokedNameFromExpression(invocation.Expression);

    private static string? PreferInvokedNameFromExpression(ExpressionSyntax expression) =>
        Unwrap(expression) switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null
        };

    private static string? PreferTypeName(TypeSyntax? type) =>
        type switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            NullableTypeSyntax n => PreferTypeName(n.ElementType),
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            _ => type == null ? null : SanitizeIdentifierSeed(type.ToString())
        };

    private static string? SanitizeIdentifierSeed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var builder = new StringBuilder(text.Length);
        var startNewWord = true;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (startNewWord && char.IsLetter(c))
                {
                    builder.Append(builder.Length == 0
                        ? char.ToLowerInvariant(c)
                        : char.ToUpperInvariant(c));
                    startNewWord = false;
                }
                else
                {
                    builder.Append(c);
                    startNewWord = false;
                }
            }
            else
            {
                startNewWord = true;
            }
        }

        var name = builder.ToString();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? FinalizeVariableName(string? seed)
    {
        if (string.IsNullOrEmpty(seed))
            return null;

        var name = seed;
        if (char.IsDigit(name[0]))
            name = "_" + name;

        if (char.IsUpper(name[0]))
            name = char.ToLowerInvariant(name[0]) + name[1..];

        if (!SyntaxIdentifierValidation.IsValidIdentifier(name))
        {
            var keywordKind = SyntaxFacts.GetKeywordKind(name);
            if (keywordKind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(keywordKind))
            {
                name = "@" + name;
            }
            else
            {
                return null;
            }
        }

        return SyntaxIdentifierValidation.IsValidIdentifier(name) ? name : null;
    }

    private async Task<bool> LinkedViewsCanHonorRewriteAsync(
        IReadOnlyList<Document> linkedDocuments,
        Document primary,
        ExpressionSyntax primaryExpression,
        ExtractVariableParams bulkParams,
        Solution beforeSolution,
        Solution afterPrimary,
        CancellationToken cancellationToken)
    {
        if (linkedDocuments.Count <= 1)
            return true;

        var primaryAfter = afterPrimary.GetDocument(primary.Id);
        if (primaryAfter == null)
            return false;
        var primaryText = await primaryAfter.GetTextAsync(cancellationToken);

        foreach (var linked in linkedDocuments)
        {
            if (linked.Id == primary.Id)
                continue;

            var sibling = beforeSolution.GetDocument(linked.Id) ?? linked;
            if (sibling is SourceGeneratedDocument)
                continue;
            if (!DocumentEditableHelpers.IsDocumentEditable(sibling, Context.Workspace))
                continue;

            var root = await sibling.GetSyntaxRootAsync(cancellationToken);
            var model = await sibling.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                return false;

            var rematched = root.DescendantNodes()
                .OfType<ExpressionSyntax>()
                .Select(Unwrap)
                .FirstOrDefault(expr =>
                    expr.SpanStart == primaryExpression.SpanStart &&
                    expr.Span.Length == primaryExpression.Span.Length &&
                    SyntaxFactory.AreEquivalent(expr, Unwrap(primaryExpression)));
            if (rematched == null)
                return false;

            Solution? siblingUpdated;
            try
            {
                siblingUpdated = TryExtractOne(
                    sibling,
                    root,
                    model,
                    rematched,
                    bulkParams,
                    cancellationToken);
            }
            catch (RefactoringException)
            {
                return false;
            }

            if (siblingUpdated == null)
                return false;

            var siblingDoc = siblingUpdated.GetDocument(sibling.Id);
            if (siblingDoc == null)
                return false;
            var siblingText = await siblingDoc.GetTextAsync(cancellationToken);
            if (!primaryText.ContentEquals(siblingText))
                return false;
        }

        return true;
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
        string sourceFile,
        string variableName,
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
                File = sourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Extract expression to variable '{variableName}' of type {type.ToDisplayString()}{replacementSuffix}",
                BeforeSnippet = expression.ToFullString(),
                AfterSnippet = $"{declaration.NormalizeWhitespace()}\n// ... {variableName} used in place of expression"
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

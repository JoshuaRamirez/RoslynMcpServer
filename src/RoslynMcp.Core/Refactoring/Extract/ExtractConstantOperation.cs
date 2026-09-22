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
/// Extracts a literal expression to a named constant.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and extracts every eligible compile-time literal,
/// naming each constant from that literal's value text and skipping
/// ineligible sites rather than throwing.
/// </summary>
public sealed class ExtractConstantOperation : RefactoringOperationBase<ExtractConstantParams>
{
    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "private", "protected", "internal", "public", "protected internal", "private protected"
    };

    /// <summary>
    /// Creates a new extract constant operation.
    /// </summary>
    public ExtractConstantOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractConstantParams @params) => Validate(@params);

    /// <summary>
    /// Validates extract-constant parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ExtractConstantParams @params)
    {
        if (@params.AllFiles)
        {
            if (@params.StartLine.HasValue ||
                @params.StartColumn.HasValue ||
                @params.EndLine.HasValue ||
                @params.EndColumn.HasValue ||
                !string.IsNullOrWhiteSpace(@params.ConstantName))
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with startLine, startColumn, endLine, endColumn, or constantName.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            {
                ValidateSourceFilePath(@params.SourceFile!);
            }

            if (!ValidVisibilities.Contains(@params.Visibility))
                throw new RefactoringException(ErrorCodes.InvalidVisibility, $"Invalid visibility: {@params.Visibility}");

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.ConstantName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "constantName is required.");

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

        if (!IdentifierValidation.IsValidIdentifier(@params.ConstantName!))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid constant name: {@params.ConstantName}");

        if (!ValidVisibilities.Contains(@params.Visibility))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"Invalid visibility: {@params.Visibility}");
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
        ExtractConstantParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var constantName = @params.ConstantName!;
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

        var sourceText = await document.GetTextAsync(cancellationToken);
        var startPosition = SymbolResolver.GetPosition(sourceText, startLine, startColumn);
        var endPosition = SymbolResolver.GetPosition(sourceText, endLine, endColumn);
        var span = TextSpan.FromBounds(startPosition, endPosition);

        var node = root.FindNode(span);
        var literal = FindLiteralExpression(node, span);

        if (literal == null)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFound,
                "No literal expression found at the specified location.");
        }

        var constantValue = semanticModel.GetConstantValue(literal, cancellationToken);
        if (!constantValue.HasValue)
        {
            throw new RefactoringException(
                ErrorCodes.NotCompileTimeConstant,
                "Expression is not a compile-time constant.");
        }

        var typeInfo = semanticModel.GetTypeInfo(literal, cancellationToken);
        var constantType = ResolveConstantType(typeInfo);
        if (constantType == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not determine expression type.");
        }

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            throw new RefactoringException(ErrorCodes.TypeNotFound, "Literal must be inside a type declaration.");
        }

        // Interfaces remain valid single-site targets (public const is legal in
        // default interface members). Bulk allFiles still skips interfaces via
        // CollectEligibleLiterals / TryExtractOne — that skip must not regress
        // omitted/false behavior (Codex P2).
        if (IsSpecialMinValueUnaryOperand(literal))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot extract the operand of a special minimum-value unary expression (-2147483648 / -9223372036854775808); extract the full expression or choose another literal.");
        }

        if (IsVisibilityIncompatibleWithContainingType(@params.Visibility, containingType))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidVisibility,
                $"Visibility '{@params.Visibility}' is not valid for containing type '{containingType.Identifier.Text}'.");
        }

        var bareName = SyntaxIdentifierValidation.NormalizeIdentifier(constantName);
        var containingTypeSymbolForShadow = semanticModel.GetDeclaredSymbol(containingType, cancellationToken) as INamedTypeSymbol;

        if (IsConstantTypeLessAccessibleThanVisibility(constantType, @params.Visibility, containingTypeSymbolForShadow))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidVisibility,
                $"Constant type '{constantType.ToDisplayString()}' is less accessible than requested visibility '{@params.Visibility}'.");
        }

        var existingMember = containingType.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.Text == constantName);

        if (existingMember != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Constant '{constantName}' already exists in type.");
        }

        if (WouldBeShadowedAtSite(semanticModel, literal.SpanStart, bareName, containingTypeSymbolForShadow))
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Constant name '{constantName}' would be shadowed at the extraction site.");
        }

        if (WouldRebindExistingUsesOfInheritedName(
                semanticModel, containingType, bareName, containingTypeSymbolForShadow, cancellationToken))
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Constant name '{constantName}' would hide an inherited member and rebind existing uses.");
        }

        List<LiteralExpressionSyntax> literalsToReplace;
        if (@params.ReplaceAll)
        {
            literalsToReplace = FindMatchingLiterals(containingType, literal, constantType, semanticModel, cancellationToken)
                .Where(site => !WouldBeShadowedAtSite(
                    semanticModel,
                    site.SpanStart,
                    bareName,
                    containingTypeSymbolForShadow))
                .ToList();
            if (!literalsToReplace.Contains(literal))
                literalsToReplace.Insert(0, literal);
        }
        else
        {
            literalsToReplace = new List<LiteralExpressionSyntax> { literal };
        }

        var constField = CreateConstantField(
            constantName,
            @params.Visibility,
            constantType,
            literal,
            semanticModel,
            containingType.SpanStart);

        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, sourceFile, constantName, literalsToReplace.Count, constField);
        }

        var constantRef = SyntaxFactory.IdentifierName(SyntaxFactory.Identifier(constantName));

        var newRoot = root.ReplaceNodes(
            literalsToReplace,
            (original, rewritten) => constantRef.WithTriviaFrom(original));

        var updatedContainingType = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First(t => t.SpanStart == containingType.SpanStart);

        var newContainingType = InsertConstantField(updatedContainingType, constField);
        newRoot = newRoot.ReplaceNode(updatedContainingType, newContainingType);

        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = newDocument.Project.Solution;

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
                Name = constantName,
                FullyQualifiedName = constantName,
                Kind = Contracts.Enums.SymbolKind.Constant
            },
            literalsToReplace.Count,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>IntroduceFieldOperation.ExecuteAllFilesAsync</c>
    /// / <c>IntroduceParameterOperation.ExecuteAllFilesAsync</c>) and extracts
    /// every eligible compile-time <see cref="LiteralExpressionSyntax"/> to a
    /// named constant derived from the literal value text. Optional
    /// <c>sourceFile</c> limits via <see cref="DocumentSourceFileFilter"/>.
    /// Linked documents that share a physical path are rewritten once and the
    /// same text is applied to every sibling <see cref="DocumentId"/> via
    /// <see cref="PathResolver.GetPathComparisonKey"/>. Uneditable /
    /// source-generated docs, name collisions, non-constants, literals outside
    /// a type, empty/invalid derived names, and otherwise ineligible targets
    /// are skipped rather than failing the walk. Deterministic
    /// <c>SpanStart</c> order within a file. When every file is a no-op,
    /// succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ExtractConstantParams @params,
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
            allDocuments = FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = allDocuments
            .GroupBy(d => PathResolver.GetPathComparisonKey(d.FilePath!), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                .ToList())
            .OrderBy(group => group[0].FilePath, StringComparer.Ordinal)
            .ToList();

        var extractedCountByDoc = new Dictionary<DocumentId, int>();
        var replaceAllIntroducedConstants = new HashSet<string>(StringComparer.Ordinal);

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
                string? updatedExtractedConstantKey = null;
                foreach (var literal in CollectEligibleLiterals(root, semanticModel, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extractedConstantKey = @params.ReplaceAll
                        ? TryCreateExtractedConstantKey(semanticModel, literal, cancellationToken)
                        : null;

                    try
                    {
                        updated = TryExtractOne(
                            currentDocument,
                            root,
                            semanticModel,
                            literal,
                            @params,
                            replaceAllIntroducedConstants,
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
                            literal,
                            @params,
                            replaceAllIntroducedConstants,
                            currentSolution,
                            updated,
                            cancellationToken))
                    {
                        // Sibling linked project cannot accept the same rewrite
                        // (e.g. name collision under different refs/preprocessor) —
                        // skip-not-throw rather than copying invalid text (Codex P1).
                        updated = null;
                        continue;
                    }

                    if (updated != null)
                    {
                        updatedExtractedConstantKey = extractedConstantKey;
                        break;
                    }
                }

                if (updated == null)
                    break;

                if (updatedExtractedConstantKey != null)
                {
                    replaceAllIntroducedConstants.Add(updatedExtractedConstantKey);
                }

                var beforeSolution = currentSolution;
                currentSolution = updated;
                var changedDocIds = new HashSet<DocumentId>();
                var changedPathKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var projectChanges in updated.GetChanges(beforeSolution).GetProjectChanges())
                {
                    foreach (var docId in projectChanges.GetChangedDocuments())
                    {
                        changedDocIds.Add(docId);
                        var changedDoc = updated.GetDocument(docId);
                        if (changedDoc?.FilePath != null)
                            changedPathKeys.Add(PathResolver.GetPathComparisonKey(changedDoc.FilePath));
                    }
                }

                if (changedPathKeys.Count > 0)
                {
                    var allCurrent = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .Where(d => d.FilePath != null)
                        .ToList();

                    foreach (var pathKey in changedPathKeys)
                    {
                        var siblings = allCurrent
                            .Where(d => PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                            .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                            .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                            .ToList();
                        if (siblings.Count <= 1)
                            continue;

                        var sourceDoc = siblings.FirstOrDefault(d =>
                                changedDocIds.Contains(d.Id) &&
                                d is not SourceGeneratedDocument &&
                                DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace))
                            ?? siblings.FirstOrDefault(d =>
                                d is not SourceGeneratedDocument &&
                                DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace));
                        if (sourceDoc == null)
                            continue;

                        var live = currentSolution.GetDocument(sourceDoc.Id);
                        if (live == null)
                            continue;
                        var sharedText = await live.GetTextAsync(cancellationToken);

                        foreach (var sibling in siblings)
                        {
                            if (sibling.Id == sourceDoc.Id)
                                continue;
                            var siblingLive = currentSolution.GetDocument(sibling.Id);
                            if (siblingLive == null || siblingLive is SourceGeneratedDocument)
                                continue;
                            if (!DocumentEditableHelpers.IsDocumentEditable(siblingLive, Context.Workspace))
                                continue;
                            currentSolution = currentSolution.WithDocumentText(sibling.Id, sharedText);
                        }
                    }
                }

                extractedCountByDoc[primary.Id] =
                    extractedCountByDoc.GetValueOrDefault(primary.Id) + 1;
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
                        : "Update extract_constant rewrites",
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
    /// <paramref name="extractedCount"/> constants.
    /// </summary>
    internal static string BuildAllFilesDescription(int extractedCount) =>
        extractedCount == 1
            ? "Extract constant"
            : $"Extract {extractedCount} constants";

    /// <summary>
    /// Collects every <see cref="LiteralExpressionSyntax"/> in
    /// <paramref name="root"/> that is a compile-time constant inside a type
    /// declaration, outside attribute arguments, and not already the initializer
    /// of a <c>const</c> field. Deterministic <c>SpanStart</c> then
    /// span-length order.
    /// </summary>
    internal static IReadOnlyList<LiteralExpressionSyntax> CollectEligibleLiterals(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(literal =>
            {
                var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null)
                    return false;

                // Enum member initializers are not const-field extract targets.
                if (literal.Ancestors().OfType<EnumDeclarationSyntax>().Any())
                    return false;

                if (IsSpecialMinValueUnaryOperand(literal))
                    return false;

                if (literal.Ancestors().OfType<AttributeSyntax>().Any())
                    return false;

                // Already part of a const field — do not re-extract.
                if (literal.Ancestors().OfType<FieldDeclarationSyntax>()
                    .Any(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)))
                {
                    return false;
                }

                if (literal.IsKind(SyntaxKind.NullLiteralExpression) ||
                    literal.IsKind(SyntaxKind.DefaultLiteralExpression))
                {
                    return false;
                }

                var constantValue = semanticModel.GetConstantValue(literal, cancellationToken);
                if (!constantValue.HasValue)
                    return false;

                var typeInfo = semanticModel.GetTypeInfo(literal, cancellationToken);
                return ResolveConstantType(typeInfo) != null;
            })
            .OrderBy(literal => literal.SpanStart)
            .ThenBy(literal => literal.Span.Length)
            .ToList();

    private Solution? TryExtractOne(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        LiteralExpressionSyntax literal,
        ExtractConstantParams bulkParams,
        ISet<string> replaceAllIntroducedConstants,
        CancellationToken cancellationToken)
    {
        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        var constantName = DeriveConstantNameFromLiteral(literal);
        if (constantName == null)
            return null;

        var constantValue = semanticModel.GetConstantValue(literal, cancellationToken);
        if (!constantValue.HasValue)
            return null;

        var typeInfo = semanticModel.GetTypeInfo(literal, cancellationToken);
        var constantType = ResolveConstantType(typeInfo);
        if (constantType == null)
            return null;

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return null;

        if (containingType is InterfaceDeclarationSyntax &&
            !string.Equals(bulkParams.Visibility, "public", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (literal.Ancestors().OfType<EnumDeclarationSyntax>().Any())
            return null;

        if (IsSpecialMinValueUnaryOperand(literal))
            return null;

        if (IsVisibilityIncompatibleWithContainingType(bulkParams.Visibility, containingType))
            return null;

        var bareName = SyntaxIdentifierValidation.NormalizeIdentifier(constantName);
        var containingTypeSymbol = semanticModel.GetDeclaredSymbol(containingType, cancellationToken) as INamedTypeSymbol;

        if (IsConstantTypeLessAccessibleThanVisibility(constantType, bulkParams.Visibility, containingTypeSymbol))
            return null;
        var canReuseReplaceAllConstant =
            bulkParams.ReplaceAll &&
            CanReuseReplaceAllConstant(
                replaceAllIntroducedConstants,
                containingTypeSymbol,
                bareName,
                constantType,
                constantValue.Value);
        if (containingTypeSymbol != null &&
            !canReuseReplaceAllConstant &&
            (containingTypeSymbol.GetMembers(bareName).Length > 0 ||
             string.Equals(containingTypeSymbol.Name, bareName, StringComparison.Ordinal)))
        {
            // Skip when any member (field/property/method/nested type/event, including
            // other partial declarations) already uses this name — syntax-only field
            // checks miss non-field members and cross-partial collisions (Codex P1).
            // Also skip when the derived name equals the containing type (CS0542) (Codex P2).
            return null;
        }

        if (!canReuseReplaceAllConstant &&
            WouldRebindExistingUsesOfInheritedName(
                semanticModel, containingType, bareName, containingTypeSymbol, cancellationToken))
        {
            return null;
        }

        List<LiteralExpressionSyntax> candidates;
        if (bulkParams.ReplaceAll)
        {
            candidates = FindMatchingLiterals(containingType, literal, constantType, semanticModel, cancellationToken);
        }
        else
        {
            candidates = new List<LiteralExpressionSyntax> { literal };
        }

        // Drop sites where an unqualified identifier would bind to a local/
        // parameter/range-variable OR a nested-type member of the derived name
        // instead of the new constant (Codex P1). Skip when the seed literal
        // itself is shadowed.
        var literalsToReplace = candidates
            .Where(site => !WouldBeShadowedAtSite(
                semanticModel, site.SpanStart, bareName, containingTypeSymbol))
            .ToList();
        if (!literalsToReplace.Contains(literal))
            return null;

        var constField = CreateConstantField(
            constantName,
            bulkParams.Visibility,
            constantType,
            literal,
            semanticModel,
            containingType.SpanStart);

        var constantRef = SyntaxFactory.IdentifierName(SyntaxFactory.Identifier(constantName));

        var newRoot = root.ReplaceNodes(
            literalsToReplace,
            (original, rewritten) => constantRef.WithTriviaFrom(original));

        if (canReuseReplaceAllConstant)
            return document.WithSyntaxRoot(newRoot).Project.Solution;

        // Rematch by span-start identity only — name fallback can pick an
        // unrelated same-named type declaration (Codex P1).
        var updatedContainingType = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.SpanStart == containingType.SpanStart);
        if (updatedContainingType == null)
            return null;

        var newContainingType = InsertConstantField(updatedContainingType, constField);
        newRoot = newRoot.ReplaceNode(updatedContainingType, newContainingType);

        return document.WithSyntaxRoot(newRoot).Project.Solution;
    }

    /// <summary>
    /// Prefers <see cref="TypeInfo.ConvertedType"/> when the literal is
    /// contextually converted to an enum (e.g. <c>State value = 0</c>) or a
    /// nullable enum (<c>State? value = 0</c>) so the extracted constant keeps
    /// the underlying enum type rather than <c>int</c> (Codex P1).
    /// </summary>
    private static ITypeSymbol? ResolveConstantType(TypeInfo typeInfo)
    {
        if (typeInfo.ConvertedType is { TypeKind: TypeKind.Enum } converted)
            return converted;

        // Nullable&lt;TEnum&gt;: ConvertedType.TypeKind is Struct, not Enum.
        if (typeInfo.ConvertedType is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                TypeArguments: [{ TypeKind: TypeKind.Enum } underlying]
            })
        {
            return underlying;
        }

        return typeInfo.Type;
    }

    /// <summary>
    /// True when <paramref name="visibility"/> cannot be applied to
    /// <paramref name="containingType"/> (static classes / structs reject
    /// protected-family members) — bulk must skip those sites (Codex P2).
    /// </summary>
    internal static bool IsVisibilityIncompatibleWithContainingType(
        string visibility,
        TypeDeclarationSyntax containingType)
    {
        var normalized = visibility.ToLowerInvariant();
        var needsInheritance =
            normalized is "protected" or "protected internal" or "private protected";
        if (!needsInheritance)
            return false;

        if (containingType is ClassDeclarationSyntax { Modifiers: var classMods } &&
            classMods.Any(SyntaxKind.StaticKeyword))
        {
            return true;
        }

        if (containingType is StructDeclarationSyntax)
            return true;

        if (containingType is RecordDeclarationSyntax record &&
            record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Derives a PascalCase-ish valid identifier from a literal's value text.
    /// Returns <see langword="null"/> when the name would be empty, invalid, or
    /// an unfixable keyword collision.
    /// </summary>
    internal static string? DeriveConstantNameFromLiteral(LiteralExpressionSyntax literal)
    {
        if (literal.IsKind(SyntaxKind.NullLiteralExpression) ||
            literal.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            return null;
        }

        var valueText = literal.Token.ValueText;
        if (string.IsNullOrEmpty(valueText))
            return null;

        var builder = new StringBuilder(valueText.Length);
        var startNewWord = true;
        foreach (var c in valueText)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (startNewWord && char.IsLetter(c))
                {
                    builder.Append(char.ToUpperInvariant(c));
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
        if (string.IsNullOrEmpty(name))
            return null;

        if (char.IsDigit(name[0]))
            name = "_" + name;

        if (!SyntaxIdentifierValidation.IsValidIdentifier(name))
        {
            // Reserved keyword without @ — try verbatim escape.
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


    /// <summary>
    /// True when <paramref name="bareName"/> already binds in scope at
    /// <paramref name="position"/> to a local, parameter, range variable, local
    /// function, type parameter, or a member declared on a type nested within
    /// <paramref name="targetContainingType"/> that would still capture an
    /// unqualified constant reference after the target declares the constant.
    /// Inherited base members alone are not shadows (the new const can hide them);
    /// use <see cref="WouldRebindExistingUsesOfInheritedName"/> to refuse hide when
    /// existing uses in the type would change binding (Codex P1).
    /// </summary>
    private static bool WouldBeShadowedAtSite(
        SemanticModel semanticModel,
        int position,
        string bareName,
        INamedTypeSymbol? targetContainingType)
    {
        foreach (var symbol in semanticModel.LookupSymbols(position, name: bareName))
        {
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
                return true;

            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction })
                return true;

            if (symbol is ITypeParameterSymbol)
                return true;

            // Inherited base members are fine — a new const on the target type
            // hides them. Only nested-type members (declared on a type nested
            // inside the extraction target) still capture an unqualified name
            // (Codex P2).
            if (symbol.ContainingType != null &&
                targetContainingType != null &&
                !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, targetContainingType) &&
                IsNamedTypeNestedWithin(symbol.ContainingType, targetContainingType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when declaring a new const named <paramref name="bareName"/> on
    /// <paramref name="containingType"/> would hide an inherited/enclosing member
    /// and rebind at least one existing use of that name inside the type (Codex P1).
    /// Hiding with no prior uses remains allowed. Explicit <c>base.</c> accesses are
    /// ignored because they keep binding to the base member after the hide.
    /// </summary>
    private static bool WouldRebindExistingUsesOfInheritedName(
        SemanticModel semanticModel,
        TypeDeclarationSyntax containingType,
        string bareName,
        INamedTypeSymbol? containingTypeSymbol,
        CancellationToken cancellationToken)
    {
        if (containingTypeSymbol == null || string.IsNullOrEmpty(bareName))
            return false;

        // Lookup at the type body to find inherited/enclosing same-name members.
        var lookupPos = containingType.OpenBraceToken.SpanStart;
        if (lookupPos <= 0)
            lookupPos = containingType.SpanStart;

        var hideTargets = new List<ISymbol>();
        foreach (var symbol in semanticModel.LookupSymbols(lookupPos, name: bareName))
        {
            if (symbol.ContainingType == null)
                continue;
            if (SymbolEqualityComparer.Default.Equals(symbol.ContainingType, containingTypeSymbol))
                continue;
            // Only inherited bases / enclosing outer types — nested-within still
            // shadows via WouldBeShadowedAtSite (Codex P1/P2).
            if (!IsBaseOrEnclosingTypeOf(symbol.ContainingType, containingTypeSymbol))
                continue;
            hideTargets.Add(symbol);
        }

        if (hideTargets.Count == 0)
            return false;

        foreach (var id in containingType.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!string.Equals(id.Identifier.ValueText, bareName, StringComparison.Ordinal))
                continue;

            // base._42 keeps binding to the base member after a Derived hide.
            if (id.Parent is MemberAccessExpressionSyntax
                {
                    Expression: BaseExpressionSyntax,
                    Name: var memberName
                } &&
                ReferenceEquals(memberName, id))
            {
                continue;
            }

            var info = semanticModel.GetSymbolInfo(id, cancellationToken);
            var bound = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (bound == null)
                continue;

            foreach (var hide in hideTargets)
            {
                if (SymbolEqualityComparer.Default.Equals(bound, hide) ||
                    SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, hide.OriginalDefinition))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a base type or an enclosing
    /// (outer) type of <paramref name="target"/>.
    /// </summary>
    private static bool IsBaseOrEnclosingTypeOf(INamedTypeSymbol candidate, INamedTypeSymbol target)
    {
        for (var current = target.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, candidate))
                return true;
        }

        for (var current = target.ContainingType; current != null; current = current.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True for the operand of the special minimum-value unary forms
    /// <c>-2147483648</c> / <c>-9223372036854775808</c> (including digit
    /// separators), where the literal is typed as unsigned and replacing only
    /// the operand changes semantics (Codex P2).
    /// </summary>
    internal static bool IsSpecialMinValueUnaryOperand(LiteralExpressionSyntax literal)
    {
        if (literal.Parent is not PrefixUnaryExpressionSyntax unary ||
            !unary.IsKind(SyntaxKind.UnaryMinusExpression) ||
            !ReferenceEquals(unary.Operand, literal))
        {
            return false;
        }

        // Use the token's numeric value so spellings with digit separators
        // (e.g. -2_147_483_648) still match — Token.Text alone misses those.
        return literal.Token.Value switch
        {
            uint u when u == 2147483648u => true,
            ulong ul when ul == 9223372036854775808ul => true,
            _ => false
        };
    }

    /// <summary>
    /// True when <paramref name="constantType"/> is not at least as accessible
    /// as the requested member <paramref name="visibility"/> after that visibility
    /// is capped by the containing type's effective accessibility (a public const
    /// on an internal type is effectively internal). Also covers private nested
    /// enums with public const and incomparable protected/internal (Codex P2).
    /// </summary>
    internal static bool IsConstantTypeLessAccessibleThanVisibility(
        ITypeSymbol constantType,
        string visibility,
        INamedTypeSymbol? containingTypeSymbol)
    {
        var memberAccessibility = visibility.ToLowerInvariant() switch
        {
            "public" => Accessibility.Public,
            "protected" => Accessibility.Protected,
            "internal" => Accessibility.Internal,
            "protected internal" => Accessibility.ProtectedOrInternal,
            "private protected" => Accessibility.ProtectedAndInternal,
            _ => Accessibility.Private
        };

        if (containingTypeSymbol != null)
        {
            var containerEffective = ContextValidTypeHelpers.GetEffectiveAccessibility(containingTypeSymbol);
            memberAccessibility = ContextValidTypeHelpers.IntersectAccessibility(
                memberAccessibility,
                containerEffective);
        }

        if (memberAccessibility is Accessibility.Private or Accessibility.NotApplicable)
            return false;

        return !TypeIsAtLeastAsAccessibleAs(constantType, memberAccessibility, containingTypeSymbol);
    }

    /// <summary>
    /// C# accessibility lattice: public &gt; protected internal &gt; {protected,
    /// internal} (incomparable siblings) &gt; private protected &gt; private.
    /// A total ordinal rank wrongly treats internal as more accessible than
    /// protected (Codex P2).
    /// </summary>
    internal static bool IsAtLeastAsAccessible(Accessibility typeAccess, Accessibility memberAccess)
    {
        if (memberAccess is Accessibility.Private or Accessibility.NotApplicable)
            return true;
        if (typeAccess == Accessibility.Public)
            return true;
        if (typeAccess == memberAccess)
            return true;

        return memberAccess switch
        {
            Accessibility.ProtectedOrInternal => false, // only public (handled) or same
            Accessibility.Protected => typeAccess == Accessibility.ProtectedOrInternal,
            Accessibility.Internal => typeAccess == Accessibility.ProtectedOrInternal,
            Accessibility.ProtectedAndInternal => typeAccess is Accessibility.ProtectedOrInternal
                or Accessibility.Protected
                or Accessibility.Internal,
            _ => false
        };
    }

    private static bool TypeIsAtLeastAsAccessibleAs(
        ITypeSymbol type,
        Accessibility required,
        INamedTypeSymbol? memberContainingType)
    {
        if (type is IArrayTypeSymbol array)
            return TypeIsAtLeastAsAccessibleAs(array.ElementType, required, memberContainingType);

        if (type is IPointerTypeSymbol pointer)
            return TypeIsAtLeastAsAccessibleAs(pointer.PointedAtType, required, memberContainingType);

        if (type is INamedTypeSymbol named)
        {
            var effective = ContextValidTypeHelpers.GetEffectiveAccessibility(named);
            if (!IsAtLeastAsAccessible(effective, required))
                return false;

            // Equal protected-family Accessibility values are not enough — a
            // protected enum on Outer is not safe for protected const on a
            // public nested Inner (CS0052), but is safe when the member type
            // derives from the enum's declaring type (Codex P2).
            if (IsProtectedFamily(required) && IsProtectedFamily(effective) &&
                memberContainingType != null &&
                !IsProtectedTypeAccessibleFromMemberContainer(named, memberContainingType))
            {
                return false;
            }

            foreach (var argument in named.TypeArguments)
            {
                if (!TypeIsAtLeastAsAccessibleAs(argument, required, memberContainingType))
                    return false;
            }
        }

        return true;
    }

    private static bool IsProtectedFamily(Accessibility accessibility) =>
        accessibility is Accessibility.Protected
            or Accessibility.ProtectedOrInternal
            or Accessibility.ProtectedAndInternal;

    /// <summary>
    /// True when <paramref name="type"/> is <paramref name="container"/> or is
    /// nested (directly or indirectly) inside it.
    /// </summary>
    private static bool IsNamedTypeNestedWithin(INamedTypeSymbol type, INamedTypeSymbol container)
    {
        for (var current = type; current != null; current = current.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, container))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Protected-family type accessibility for a protected-family member:
    /// allow when the type is nested in the member container, the member
    /// container derives from the type's declaring type, or the member
    /// container is nested under the type's declaring type with effective
    /// accessibility private / protected / private-protected (not assembly-visible).
    /// </summary>
    private static bool IsProtectedTypeAccessibleFromMemberContainer(
        INamedTypeSymbol type,
        INamedTypeSymbol memberContainer)
    {
        if (IsNamedTypeNestedWithin(type, memberContainer))
            return true;

        var typeContainer = type.ContainingType;
        if (typeContainer == null)
            return true;

        for (var current = memberContainer; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, typeContainer))
                return true;
        }

        for (var current = memberContainer; current != null; current = current.ContainingType)
        {
            if (!SymbolEqualityComparer.Default.Equals(current, typeContainer))
                continue;

            // Every containing type from memberContainer up to typeContainer must
            // keep the access domain within typeContainer's protected-family
            // domain. A private effective type seals access immediately;
            // protected/private-protected preserve the domain; public/internal/
            // protected-internal leak it (e.g. public Mid { protected Inner }).
            for (var nested = memberContainer; nested != null && !SymbolEqualityComparer.Default.Equals(nested, typeContainer); nested = nested.ContainingType)
            {
                var effectiveNestedAccessibility = ContextValidTypeHelpers.GetEffectiveAccessibility(nested);
                if (effectiveNestedAccessibility == Accessibility.Private)
                    return true;
                if (effectiveNestedAccessibility is Accessibility.Protected or Accessibility.ProtectedAndInternal)
                    continue;

                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that every linked sibling document can apply an equivalent
    /// extract for the same literal span before shared text is propagated.
    /// Returns false (caller skips) when any editable sibling cannot honor the
    /// rewrite or would produce divergent text (Codex P1).
    /// </summary>
    private async Task<bool> LinkedViewsCanHonorRewriteAsync(
        IReadOnlyList<Document> linkedDocuments,
        Document primary,
        LiteralExpressionSyntax primaryLiteral,
        ExtractConstantParams bulkParams,
        ISet<string> replaceAllIntroducedConstants,
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
                .OfType<LiteralExpressionSyntax>()
                .FirstOrDefault(lit =>
                    lit.SpanStart == primaryLiteral.SpanStart &&
                    lit.Kind() == primaryLiteral.Kind() &&
                    lit.Token.ValueText == primaryLiteral.Token.ValueText);
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
                    replaceAllIntroducedConstants,
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

    private static bool CanReuseReplaceAllConstant(
        ISet<string> replaceAllIntroducedConstants,
        INamedTypeSymbol? containingTypeSymbol,
        string bareName,
        ITypeSymbol constantType,
        object? constantValue)
    {
        if (containingTypeSymbol == null)
            return false;

        var extractedConstantKey = CreateExtractedConstantKey(containingTypeSymbol, bareName);
        if (extractedConstantKey == null ||
            !replaceAllIntroducedConstants.Contains(extractedConstantKey))
        {
            return false;
        }

        var members = containingTypeSymbol.GetMembers(bareName);
        if (members.Length != 1 || members[0] is not IFieldSymbol { IsConst: true, HasConstantValue: true } field)
            return false;

        return SymbolEqualityComparer.Default.Equals(field.Type, constantType) &&
               Equals(field.ConstantValue, constantValue);
    }

    private static string? TryCreateExtractedConstantKey(
        SemanticModel semanticModel,
        LiteralExpressionSyntax literal,
        CancellationToken cancellationToken)
    {
        var constantName = DeriveConstantNameFromLiteral(literal);
        if (constantName == null)
            return null;

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return null;

        var containingTypeSymbol = semanticModel.GetDeclaredSymbol(containingType, cancellationToken) as INamedTypeSymbol;
        return containingTypeSymbol == null
            ? null
            : CreateExtractedConstantKey(containingTypeSymbol, SyntaxIdentifierValidation.NormalizeIdentifier(constantName));
    }

    private static string? CreateExtractedConstantKey(INamedTypeSymbol containingTypeSymbol, string bareName)
    {
        // Prefer stable symbol identity over declaring Span.Start — inserting a
        // field into an earlier type in the same file shifts later partial
        // declaration offsets and would otherwise invalidate replaceAll reuse
        // keys mid-walk (Codex P2).
        var typeKey = containingTypeSymbol.OriginalDefinition
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (string.IsNullOrWhiteSpace(typeKey))
            return null;

        if (containingTypeSymbol.IsFileLocal)
        {
            var declaringFile = containingTypeSymbol.DeclaringSyntaxReferences
                .Select(reference => reference.SyntaxTree.FilePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            typeKey = string.IsNullOrWhiteSpace(declaringFile)
                ? typeKey + "\0file"
                : typeKey + "\0file\0" + PathResolver.GetPathComparisonKey(declaringFile);
        }

        return typeKey + "::" + bareName;
    }

    private static LiteralExpressionSyntax? FindLiteralExpression(SyntaxNode node, TextSpan span)
    {
        var current = node;
        while (current != null)
        {
            if (current is LiteralExpressionSyntax literal && current.Span.Contains(span))
            {
                return literal;
            }
            current = current.Parent;
        }
        return null;
    }

    private static List<LiteralExpressionSyntax> FindMatchingLiterals(
        TypeDeclarationSyntax containingType,
        LiteralExpressionSyntax originalLiteral,
        ITypeSymbol constantType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        return containingType.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(lit =>
            {
                // Do not rewrite nested type bodies (Codex P1) — a nested type may
                // already declare the derived name, and replaceAll must stay in the
                // containing type that receives the new constant.
                if (lit.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() != containingType)
                    return false;

                // Skip special min-value unary operands even when text/type match
                // a non-unary seed (Codex P2).
                if (IsSpecialMinValueUnaryOperand(lit))
                    return false;

                if (lit.Kind() != originalLiteral.Kind()) return false;
                if (lit.Token.ValueText != originalLiteral.Token.ValueText) return false;

                // replaceAll must keep contextual/semantic type compatibility
                // (e.g. do not rewrite int 0 sites when the constant is State) (Codex P1).
                var candidateType = ResolveConstantType(semanticModel.GetTypeInfo(lit, cancellationToken));
                return candidateType != null &&
                       SymbolEqualityComparer.Default.Equals(candidateType, constantType);
            })
            .ToList();
    }

    private static FieldDeclarationSyntax CreateConstantField(
        string name,
        string visibility,
        ITypeSymbol type,
        LiteralExpressionSyntax initializer,
        SemanticModel semanticModel,
        int insertionPosition)
    {
        var modifiers = new List<SyntaxToken>();

        switch (visibility.ToLowerInvariant())
        {
            case "public":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                break;
            case "protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            case "internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "protected internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "private protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            default:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                break;
        }

        modifiers.Add(SyntaxFactory.Token(SyntaxKind.ConstKeyword));

        var typeName = FormatConstantTypeName(type, semanticModel, insertionPosition);
        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName(typeName).WithTrailingTrivia(SyntaxFactory.Space))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(initializer)))))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Context-valid type spelling for the inserted const field. When a
    /// namespace segment of the ordinary display is shadowed at the insertion
    /// site (e.g. local type <c>External</c> vs <c>global::External.State</c>),
    /// fall back to a <c>global::</c>-qualified display (Codex P2).
    /// </summary>
    private static string FormatConstantTypeName(
        ITypeSymbol type,
        SemanticModel semanticModel,
        int insertionPosition)
    {
        var display = ContextValidTypeHelpers.ToContextValidTypeName(type, semanticModel, insertionPosition);
        if (!NamespaceEqualityHelpers.TypeNameBindsToDifferentType(
                display, type, semanticModel, insertionPosition))
        {
            return display;
        }

        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static TypeDeclarationSyntax InsertConstantField(
        TypeDeclarationSyntax typeDeclaration,
        FieldDeclarationSyntax constField)
    {
        // Semicolon-bodied positional records (e.g. record R(int X = 42);) have
        // no member body braces — WithMembers alone leaves an invalid declaration
        // (Codex P1). Convert to a braced form before inserting.
        typeDeclaration = EnsureMemberBodyBraces(typeDeclaration);

        var members = typeDeclaration.Members.ToList();

        var insertIndex = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is FieldDeclarationSyntax field &&
                field.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                insertIndex = i + 1;
            }
            else if (insertIndex > 0)
            {
                break;
            }
        }

        members.Insert(insertIndex, constField
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    /// <summary>
    /// Converts a semicolon-bodied type declaration (no open brace) into a
    /// braced form so members can be inserted validly (Codex P1).
    /// </summary>
    internal static TypeDeclarationSyntax EnsureMemberBodyBraces(TypeDeclarationSyntax typeDeclaration)
    {
        if (!typeDeclaration.OpenBraceToken.IsKind(SyntaxKind.None))
            return typeDeclaration;

        var open = SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
            .WithLeadingTrivia(SyntaxFactory.Space)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        if (typeDeclaration is RecordDeclarationSyntax record)
        {
            var semicolon = record.SemicolonToken;
            // Preserve semicolon leading trivia (comments/directives) on the
            // close brace before discarding the semicolon token (Codex P2).
            var close = SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                .WithLeadingTrivia(semicolon.LeadingTrivia)
                .WithTrailingTrivia(semicolon.TrailingTrivia);
            return record
                .WithSemicolonToken(default)
                .WithOpenBraceToken(open)
                .WithCloseBraceToken(close);
        }

        var closeBrace = SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        return typeDeclaration
            .WithOpenBraceToken(open)
            .WithCloseBraceToken(closeBrace);
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string sourceFile,
        string constantName,
        int replacementCount,
        FieldDeclarationSyntax constField)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = sourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Extract constant '{constantName}' ({replacementCount} replacement{(replacementCount > 1 ? "s" : "")})",
                BeforeSnippet = "// (literal values)",
                AfterSnippet = constField.NormalizeWhitespace().ToFullString()
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

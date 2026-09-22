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
                if (!File.Exists(@params.SourceFile!))
                    throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
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
        if (typeInfo.Type == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not determine expression type.");
        }

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            throw new RefactoringException(ErrorCodes.TypeNotFound, "Literal must be inside a type declaration.");
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

        List<LiteralExpressionSyntax> literalsToReplace;
        if (@params.ReplaceAll)
        {
            literalsToReplace = FindMatchingLiterals(containingType, literal);
        }
        else
        {
            literalsToReplace = new List<LiteralExpressionSyntax> { literal };
        }

        var constField = CreateConstantField(
            constantName,
            @params.Visibility,
            typeInfo.Type,
            literal);

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
            .First(t => t.Identifier.Text == containingType.Identifier.Text);

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
                foreach (var literal in CollectEligibleLiterals(root, semanticModel, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        updated = TryExtractOne(
                            currentDocument,
                            root,
                            semanticModel,
                            literal,
                            @params,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        updated = null;
                    }

                    if (updated != null)
                        break;
                }

                if (updated == null)
                    break;

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
            _ when !string.Equals(distinctPaths[0], sourceFileKey, StringComparison.Ordinal) && !File.Exists(sourceFile) =>
                throw new RefactoringException(
                    ErrorCodes.SourceFileNotFound,
                    $"Source file not found: {sourceFile}"),
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
                if (literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == null)
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
                return typeInfo.Type != null;
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
        if (typeInfo.Type == null)
            return null;

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return null;

        var existingMember = containingType.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .FirstOrDefault(v =>
                string.Equals(
                    SyntaxIdentifierValidation.NormalizeIdentifier(v.Identifier.Text),
                    SyntaxIdentifierValidation.NormalizeIdentifier(constantName),
                    StringComparison.Ordinal));

        if (existingMember != null)
            return null;

        List<LiteralExpressionSyntax> literalsToReplace;
        if (bulkParams.ReplaceAll)
        {
            literalsToReplace = FindMatchingLiterals(containingType, literal);
        }
        else
        {
            literalsToReplace = new List<LiteralExpressionSyntax> { literal };
        }

        var constField = CreateConstantField(
            constantName,
            bulkParams.Visibility,
            typeInfo.Type,
            literal);

        var constantRef = SyntaxFactory.IdentifierName(SyntaxFactory.Identifier(constantName));

        var newRoot = root.ReplaceNodes(
            literalsToReplace,
            (original, rewritten) => constantRef.WithTriviaFrom(original));

        var updatedContainingType = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t =>
                t.SpanStart == containingType.SpanStart ||
                t.Identifier.Text == containingType.Identifier.Text);
        if (updatedContainingType == null)
            return null;

        var newContainingType = InsertConstantField(updatedContainingType, constField);
        newRoot = newRoot.ReplaceNode(updatedContainingType, newContainingType);

        return document.WithSyntaxRoot(newRoot).Project.Solution;
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
        LiteralExpressionSyntax originalLiteral)
    {
        return containingType.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(lit =>
            {
                if (lit.Kind() != originalLiteral.Kind()) return false;
                return lit.Token.ValueText == originalLiteral.Token.ValueText;
            })
            .ToList();
    }

    private static FieldDeclarationSyntax CreateConstantField(
        string name,
        string visibility,
        ITypeSymbol type,
        LiteralExpressionSyntax initializer)
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

        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName(type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(initializer)))))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .NormalizeWhitespace();
    }

    private static TypeDeclarationSyntax InsertConstantField(
        TypeDeclarationSyntax typeDeclaration,
        FieldDeclarationSyntax constField)
    {
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Encapsulate;

/// <summary>
/// Encapsulates a field by converting it to a property.
/// Honors optional <c>line</c> and <c>column</c> to disambiguate
/// same-named fields in one file (identifier preferred, then smallest
/// covering declarator/field). Omitted column keeps today's fieldName +
/// optional line pick. Omitted line keeps today's field
/// <c>VariableDeclaratorSyntax</c> <c>FirstOrDefault</c> pick. Column
/// without line keeps that omitted-line first-match after the fieldName
/// filter. Locals and other non-field declarators stay excluded. Nested
/// types participate when line is set. If line is set (including
/// column+line) and no covering field matches, throw
/// <c>FieldNotFound</c> rather than falling back to first-match. After
/// the rewrite rematches the selected field, recover it from a
/// per-execution syntax annotation and strip the annotation before
/// commit. Same-file outer/sibling references are external (semantic
/// containment in the selected type, not file-path equality) and are
/// annotated before the field rewrite. Optional <c>allFiles</c> walks
/// every C# document and encapsulates every eligible field (skip
/// const / name-collision / other per-field failures rather than
/// throwing).
/// </summary>
public sealed class EncapsulateFieldOperation : RefactoringOperationBase<EncapsulateFieldParams>
{
    /// <summary>
    /// Creates a new encapsulate field operation.
    /// </summary>
    public EncapsulateFieldOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(EncapsulateFieldParams @params) => Validate(@params);

    /// <summary>
    /// Validates encapsulate-field parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(EncapsulateFieldParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.FieldName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue ||
                !string.IsNullOrWhiteSpace(@params.PropertyName))
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with fieldName, line, column, or propertyName.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.FieldName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "fieldName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.PropertyName != null && !IsValidIdentifier(@params.PropertyName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid property name: {@params.PropertyName}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        EncapsulateFieldParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Optional line/column disambiguates same-named fields. Omitted
        // column keeps today's fieldName + optional line pick. Omitted
        // line keeps today's field VariableDeclaratorSyntax FirstOrDefault.
        // Column without line keeps that omitted-line first-match after
        // the fieldName filter. Line set (including column+line) picks
        // the covering field (identifier preferred, then smallest
        // covering declarator/field). Nested types participate. Locals
        // stay excluded. Do not fall back to first-match when line is
        // set and nothing covers that position.
        var fieldName = @params.FieldName!;
        var fieldDeclarator = FindFieldDeclarator(root, fieldName, @params.Line, @params.Column);
        if (fieldDeclarator == null)
        {
            throw new RefactoringException(
                ErrorCodes.FieldNotFound,
                $"Field '{fieldName}' not found.");
        }

        var fieldDeclaration = (FieldDeclarationSyntax)fieldDeclarator.Parent!.Parent!;
        var fieldSymbol = semanticModel.GetDeclaredSymbol(fieldDeclarator, cancellationToken) as IFieldSymbol;

        if (fieldSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve field symbol.");
        }

        // Check for const
        if (fieldSymbol.IsConst)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot encapsulate const field.");
        }

        // Check for static
        var isStatic = fieldSymbol.IsStatic;

        // Determine property name
        var propertyName = @params.PropertyName ?? DerivePropertyName(fieldName);

        // Check for existing property with same name
        var containingType = fieldDeclarator.Ancestors().OfType<TypeDeclarationSyntax>().First();
        var existingProperty = containingType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName);

        if (existingProperty != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Property '{propertyName}' already exists.");
        }

        // Create property
        var property = SyntaxGenerationHelper.CreatePropertyFromField(
            fieldSymbol,
            propertyName,
            @params.ReadOnly);

        if (isStatic)
        {
            property = property.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        // Find all references to the field
        var references = await SymbolFinder.FindReferencesAsync(
            fieldSymbol,
            Context.Solution,
            cancellationToken);

        var externalReferences = references
            .SelectMany(r => r.Locations)
            .Where(loc => !IsInsideContainingType(loc, fieldSymbol.ContainingType))
            .ToList();

        // Rename field if property name would conflict (computed before preview
        // so the description can mention backing-field rename updates).
        var newFieldName = fieldName;
        var fieldRenamed = false;
        if (propertyName.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
        {
            newFieldName = "_" + char.ToLowerInvariant(fieldName[0]) + fieldName.Substring(1);
            fieldRenamed = true;
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                fieldSymbol,
                propertyName,
                property,
                externalReferences.Count,
                fieldRenamed);
        }

        var newSolution = await ApplyEncapsulateToSolutionAsync(
            document,
            root,
            fieldDeclarator,
            fieldDeclaration,
            fieldSymbol,
            fieldName,
            propertyName,
            property,
            externalReferences,
            newFieldName,
            fieldRenamed,
            @params.UpdateReferences,
            cancellationToken);

        var referenceReplacement = @params.UpdateReferences
            ? propertyName
            : fieldRenamed ? newFieldName : null;
        var referencesUpdated = referenceReplacement != null ? externalReferences.Count : 0;

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
                Name = propertyName,
                FullyQualifiedName = $"{fieldSymbol.ContainingType.ToDisplayString()}.{propertyName}",
                Kind = Contracts.Enums.SymbolKind.Property
            },
            referencesUpdated,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>AddNullChecksOperation.ExecuteAllFilesAsync</c> /
    /// <c>ConvertToAsyncOperation.ExecuteAllFilesAsync</c> /
    /// <c>ConvertPropertyOperation.ExecuteAllFilesAsync</c> /
    /// <c>InvertIfOperation.ExecuteAllFilesAsync</c>) and encapsulates
    /// every eligible field <c>VariableDeclaratorSyntax</c> (same filter
    /// as <see cref="FindFieldDeclarator"/>; locals excluded). Const
    /// fields, fields whose derived property name already exists, and
    /// other per-field failures are skipped. Property names are derived
    /// per field via <see cref="DerivePropertyName"/>. When every file
    /// is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        EncapsulateFieldParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var encapsulatedCountByDoc = new Dictionary<DocumentId, int>();

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (document is SourceGeneratedDocument)
                continue;

            while (true)
            {
                var currentDocument = currentSolution.GetDocument(document.Id);
                if (currentDocument == null || currentDocument is SourceGeneratedDocument)
                    break;

                var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null)
                    break;

                Solution? updated = null;
                foreach (var fieldDeclarator in CollectFieldDeclarators(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        updated = await TryEncapsulateOneAsync(
                            currentDocument,
                            root,
                            semanticModel,
                            fieldDeclarator,
                            @params.ReadOnly,
                            @params.UpdateReferences,
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

                currentSolution = updated;
                encapsulatedCountByDoc[document.Id] =
                    encapsulatedCountByDoc.GetValueOrDefault(document.Id) + 1;
            }
        }

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

        foreach (var document in allDocuments)
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
                var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
                var currentRoot = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                if (originalRoot == null || currentRoot == null)
                    continue;

                var span = originalRoot.GetLocation().GetLineSpan();
                var encapsulatedCount = encapsulatedCountByDoc.GetValueOrDefault(document.Id);
                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = encapsulatedCount > 0
                        ? BuildAllFilesDescription(encapsulatedCount)
                        : "Update references of encapsulated fields",
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
    /// Preview description for a file that encapsulated
    /// <paramref name="encapsulatedCount"/> fields.
    /// </summary>
    internal static string BuildAllFilesDescription(int encapsulatedCount) =>
        encapsulatedCount == 1
            ? "Encapsulate field"
            : $"Encapsulate {encapsulatedCount} fields";

    /// <summary>
    /// Collects every field <see cref="VariableDeclaratorSyntax"/> in
    /// <paramref name="root"/> using the same field-only filter as
    /// <see cref="FindFieldDeclarator"/> (locals and other non-field
    /// declarators stay excluded).
    /// </summary>
    internal static IReadOnlyList<VariableDeclaratorSyntax> CollectFieldDeclarators(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(IsFieldDeclarator)
            .ToList();

    private static bool IsFieldDeclarator(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent is FieldDeclarationSyntax;

    private async Task<Solution?> TryEncapsulateOneAsync(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        VariableDeclaratorSyntax fieldDeclarator,
        bool readOnly,
        bool updateReferences,
        CancellationToken cancellationToken)
    {
        if (fieldDeclarator.Parent?.Parent is not FieldDeclarationSyntax fieldDeclaration)
            return null;

        var fieldName = fieldDeclarator.Identifier.Text;
        var fieldSymbol = semanticModel.GetDeclaredSymbol(fieldDeclarator, cancellationToken) as IFieldSymbol;
        if (fieldSymbol == null)
            return null;

        if (fieldSymbol.IsConst)
            return null;

        var propertyName = DerivePropertyName(fieldName);

        var containingType = fieldDeclarator.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return null;

        var existingProperty = containingType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName);
        if (existingProperty != null)
            return null;

        var property = SyntaxGenerationHelper.CreatePropertyFromField(
            fieldSymbol,
            propertyName,
            readOnly);

        if (fieldSymbol.IsStatic)
            property = property.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));

        var references = await SymbolFinder.FindReferencesAsync(
            fieldSymbol,
            document.Project.Solution,
            cancellationToken);

        var externalReferences = references
            .SelectMany(r => r.Locations)
            .Where(loc => !IsInsideContainingType(loc, fieldSymbol.ContainingType))
            .ToList();

        var newFieldName = fieldName;
        var fieldRenamed = false;
        if (propertyName.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
        {
            newFieldName = "_" + char.ToLowerInvariant(fieldName[0]) + fieldName.Substring(1);
            fieldRenamed = true;
        }

        return await ApplyEncapsulateToSolutionAsync(
            document,
            root,
            fieldDeclarator,
            fieldDeclaration,
            fieldSymbol,
            fieldName,
            propertyName,
            property,
            externalReferences,
            newFieldName,
            fieldRenamed,
            updateReferences,
            cancellationToken);
    }

    private static async Task<Solution> ApplyEncapsulateToSolutionAsync(
        Document document,
        SyntaxNode root,
        VariableDeclaratorSyntax fieldDeclarator,
        FieldDeclarationSyntax fieldDeclaration,
        IFieldSymbol fieldSymbol,
        string fieldName,
        string propertyName,
        PropertyDeclarationSyntax property,
        IReadOnlyList<ReferenceLocation> externalReferences,
        string newFieldName,
        bool fieldRenamed,
        bool updateReferences,
        CancellationToken cancellationToken)
    {
        // Make field private if it's not already
        var newFieldDeclaration = fieldDeclaration;
        if (fieldSymbol.DeclaredAccessibility != Accessibility.Private)
        {
            var newModifiers = SyntaxFactory.TokenList(
                fieldDeclaration.Modifiers.Where(m =>
                    !m.IsKind(SyntaxKind.PublicKeyword) &&
                    !m.IsKind(SyntaxKind.ProtectedKeyword) &&
                    !m.IsKind(SyntaxKind.InternalKeyword))
                .Prepend(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

            newFieldDeclaration = fieldDeclaration.WithModifiers(newModifiers);
        }

        if (fieldRenamed)
        {
            var newDeclarator = fieldDeclarator.WithIdentifier(SyntaxFactory.Identifier(newFieldName));
            var variableDeclaration = (VariableDeclarationSyntax)fieldDeclarator.Parent!;
            var newVariableDeclaration = variableDeclaration.ReplaceNode(fieldDeclarator, newDeclarator);
            newFieldDeclaration = newFieldDeclaration.WithDeclaration(newVariableDeclaration);

            // Update property to use new field name
            property = UpdatePropertyFieldName(property, newFieldName);
        }

        // Redirect to the property when updateReferences is true. When false,
        // still rewrite to the renamed backing field so callers stay on the
        // field (`name` → `_name`) instead of a name that no longer exists.
        var referenceReplacement = updateReferences
            ? propertyName
            : fieldRenamed ? newFieldName : null;

        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later encapsulate on another same-named
        // field would recover the stale node via FirstOrDefault.
        // Today's rematch First by fieldName / containing type name is
        // not enough — it retargets a nested same-named field to the
        // outer declaration. Same-file external identifiers also shift
        // when the property is inserted; annotate those before the
        // rewrite rather than rematching a stale SourceSpan.
        var fieldAnnotation = new SyntaxAnnotation("encapsulate-field-target");
        var refAnnotation = new SyntaxAnnotation("encapsulate-field-external-ref");
        newFieldDeclaration = newFieldDeclaration.WithAdditionalAnnotations(fieldAnnotation);

        var replacements = new Dictionary<SyntaxNode, SyntaxNode> { [fieldDeclaration] = newFieldDeclaration };
        if (referenceReplacement != null)
        {
            foreach (var identifier in EnumerateSameFileExternalIdentifiers(
                         root, document.Id, externalReferences, fieldName))
            {
                replacements[identifier] = identifier.WithAdditionalAnnotations(refAnnotation);
            }
        }

        var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);

        var updatedFieldDecl = newRoot.GetAnnotatedNodes(fieldAnnotation)
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault()
            ?? throw new RefactoringException(
                ErrorCodes.RoslynError,
                "Could not recover the selected field after rewrite.");

        var updatedContainingType = updatedFieldDecl.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault()
            ?? throw new RefactoringException(
                ErrorCodes.RoslynError,
                "Could not recover the selected field's containing type after rewrite.");

        var fieldIndex = updatedContainingType.Members.IndexOf(updatedFieldDecl);
        var newMembers = updatedContainingType.Members.Insert(
            fieldIndex + 1,
            property.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                   .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        var newContainingType = updatedContainingType.WithMembers(newMembers);
        newRoot = newRoot.ReplaceNode(updatedContainingType, newContainingType);

        if (referenceReplacement != null)
        {
            var annotatedRefs = newRoot.GetAnnotatedNodes(refAnnotation)
                .OfType<IdentifierNameSyntax>()
                .ToList();
            if (annotatedRefs.Count > 0)
            {
                newRoot = newRoot.ReplaceNodes(
                    annotatedRefs,
                    (original, _) => SyntaxFactory.IdentifierName(referenceReplacement)
                        .WithTriviaFrom(original));
            }
        }

        var stillAnnotated = newRoot.GetAnnotatedNodes(fieldAnnotation).FirstOrDefault();
        if (stillAnnotated != null)
            newRoot = newRoot.ReplaceNode(stillAnnotated, stillAnnotated.WithoutAnnotations(fieldAnnotation));

        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        if (referenceReplacement != null)
        {
            foreach (var reference in externalReferences)
            {
                if (reference.Document.Id == document.Id)
                    continue;

                var refDoc = newSolution.GetDocument(reference.Document.Id);
                if (refDoc == null) continue;

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null) continue;

                var refNode = refRoot.FindNode(reference.Location.SourceSpan);
                if (refNode is IdentifierNameSyntax identifier &&
                    identifier.Identifier.Text == fieldName)
                {
                    var newIdentifier = SyntaxFactory.IdentifierName(referenceReplacement)
                        .WithTriviaFrom(identifier);
                    var newRefRoot = refRoot.ReplaceNode(identifier, newIdentifier);
                    newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
                }
            }
        }

        return newSolution;
    }

    /// <summary>
    /// Finds a field by <paramref name="fieldName"/>. Omitted
    /// <paramref name="column"/> keeps today's fieldName + optional
    /// <paramref name="line"/> pick, including omitted-line field
    /// <c>VariableDeclaratorSyntax</c> <c>FirstOrDefault</c> and
    /// line-only exclusive-end coverage (<see cref="SpanCoversLine"/>).
    /// Do not force column 1 when omitted. Locals and other non-field
    /// declarators stay excluded. Column without line keeps today's
    /// <c>FirstOrDefault</c> after the fieldName filter rather than
    /// substituting each candidate's own start line. When column is set
    /// with line, picks the field whose identifier or declaration span
    /// covers that 1-based column (same exclusive-end coverage as
    /// <c>UseBaseTypeOperation.SpanCoversColumn</c> /
    /// <c>PushMembersDownOperation.SpanCoversColumn</c> /
    /// <c>PullMembersUpOperation.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest covering declarator or field.
    /// Nested types participate. Do not require the declaration to start
    /// on <paramref name="line"/> when column is set — a split
    /// declaration may put the identifier on a continuation line. If
    /// column is set with line and nothing covers that position, return
    /// null (<c>FieldNotFound</c>) rather than falling back to
    /// first-match. Line-only with no covering span also returns null
    /// (today's <c>FieldNotFound</c>). After the rewrite rematches the
    /// selected field, recover it from the per-execution syntax
    /// annotation — do not reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static VariableDeclaratorSyntax? FindFieldDeclarator(
        SyntaxNode root,
        string fieldName,
        int? line,
        int? column = null)
    {
        var matches = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(IsMatchingFieldDeclarator)
            .ToList();

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name field and could silently pick the shortest. Keep
        // today's FirstOrDefault after the fieldName filter.
        if (column.HasValue && !line.HasValue)
            return matches.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // field's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest covering declarator or
            // field (nested over outer). Do not silently pick the first
            // when a covering node exists elsewhere — scan every
            // candidate. If nothing covers this position, keep today's
            // not-found (null) rather than inventing a first-match.
            return matches
                .Where(declarator => FieldCoversColumn(declarator, line!.Value, column.Value))
                .OrderBy(declarator => IdentifierCoversColumn(declarator, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(declarator => SmallestCoveringSpanLength(declarator, line!.Value, column.Value))
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return matches.FirstOrDefault();

        return matches
            .Where(declarator => FieldCoversLine(declarator, line.Value))
            .OrderBy(declarator => IdentifierCoversLine(declarator, line.Value) ? 0 : 1)
            .ThenBy(declarator => SmallestCoveringSpanLength(declarator, line.Value))
            .FirstOrDefault();

        bool IsMatchingFieldDeclarator(VariableDeclaratorSyntax declarator) =>
            declarator.Identifier.Text == fieldName &&
            IsFieldDeclarator(declarator);
    }

    private static bool FieldCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        IdentifierCoversLine(declarator, line) ||
        SpanCoversLine(declarator.GetLocation().GetLineSpan(), line) ||
        (GetFieldDeclaration(declarator) is { } field &&
         SpanCoversLine(field.GetLocation().GetLineSpan(), line));

    private static bool IdentifierCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        SpanCoversLine(declarator.Identifier.GetLocation().GetLineSpan(), line);

    private static bool FieldCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        IdentifierCoversColumn(declarator, line, column) ||
        SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column) ||
        (GetFieldDeclaration(declarator) is { } field &&
         SpanCoversColumn(field.GetLocation().GetLineSpan(), line, column));

    private static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        SpanCoversColumn(declarator.Identifier.GetLocation().GetLineSpan(), line, column);

    private static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line)
    {
        var smallest = int.MaxValue;
        if (SpanCoversLine(declarator.GetLocation().GetLineSpan(), line))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (GetFieldDeclaration(declarator) is { } field &&
            SpanCoversLine(field.GetLocation().GetLineSpan(), line))
        {
            smallest = Math.Min(smallest, field.Span.Length);
        }

        return smallest;
    }

    private static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line, int column)
    {
        var smallest = int.MaxValue;
        if (SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (GetFieldDeclaration(declarator) is { } field &&
            SpanCoversColumn(field.GetLocation().GetLineSpan(), line, column))
        {
            smallest = Math.Min(smallest, field.Span.Length);
        }

        return smallest;
    }

    private static FieldDeclarationSyntax? GetFieldDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent as FieldDeclarationSyntax;

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent field also
    /// match the previous declaration. Same helper as
    /// <c>UseBaseTypeOperation.SpanCoversColumn</c> /
    /// <c>PushMembersDownOperation.SpanCoversColumn</c> /
    /// <c>PullMembersUpOperation.SpanCoversColumn</c>.
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

    /// <summary>
    /// 1-based line coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so a span that ends at the start of a line does not
    /// cover that line. Treating the end as inclusive would let the first
    /// line of an adjacent field also match the previous declaration. Same
    /// exclusive-end idea as <c>UseBaseTypeOperation.SpanCoversLine</c>.
    /// </summary>
    internal static bool SpanCoversLine(FileLinePositionSpan span, int line)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == endLine && span.EndLinePosition.Character == 0)
            return false;
        return true;
    }

    /// <summary>
    /// True when the reference lives in the selected field's containing
    /// type (same-class / same-type). File-path equality is not enough:
    /// an outer or sibling type in the same source file is external.
    /// Innermost enclosing <c>TypeDeclarationSyntax</c> is matched to
    /// <paramref name="containingType"/>'s declaring syntax so a nested
    /// type is not treated as the outer type, and a later partial of the
    /// selected type still counts as same-type.
    /// </summary>
    internal static bool IsInsideContainingType(
        ReferenceLocation location,
        INamedTypeSymbol containingType)
    {
        var source = location.Location;
        if (!source.IsInSource || source.SourceTree == null)
            return false;

        var node = source.SourceTree.GetRoot().FindNode(source.SourceSpan, getInnermostNodeForTie: true);
        var innermost = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (innermost == null)
            return false;

        foreach (var syntaxRef in containingType.DeclaringSyntaxReferences)
        {
            if (syntaxRef.SyntaxTree == innermost.SyntaxTree && syntaxRef.Span == innermost.Span)
                return true;
        }

        return false;
    }

    private static IEnumerable<IdentifierNameSyntax> EnumerateSameFileExternalIdentifiers(
        SyntaxNode root,
        DocumentId documentId,
        IEnumerable<ReferenceLocation> externalReferences,
        string fieldName)
    {
        var seen = new HashSet<IdentifierNameSyntax>();
        foreach (var reference in externalReferences)
        {
            if (reference.Document.Id != documentId)
                continue;

            var refNode = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
            if (refNode is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == fieldName &&
                seen.Add(identifier))
            {
                yield return identifier;
            }
        }
    }

    /// <summary>
    /// Derives a property name from <paramref name="fieldName"/> by
    /// stripping a leading underscore and capitalizing the first letter.
    /// </summary>
    internal static string DerivePropertyName(string fieldName)
    {
        // Remove leading underscore if present
        var name = fieldName.TrimStart('_');

        // Capitalize first letter
        if (name.Length > 0)
        {
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        return name;
    }

    private static PropertyDeclarationSyntax UpdatePropertyFieldName(
        PropertyDeclarationSyntax property,
        string newFieldName)
    {
        return property.ReplaceNodes(
            property.DescendantNodes().OfType<IdentifierNameSyntax>(),
            (original, rewritten) =>
            {
                // Check if this identifier is the field reference in getter/setter
                if (original.Parent is ReturnStatementSyntax ||
                    (original.Parent is AssignmentExpressionSyntax assign && assign.Left == original))
                {
                    return SyntaxFactory.IdentifierName(newFieldName).WithTriviaFrom(original);
                }
                return rewritten;
            });
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        EncapsulateFieldParams @params,
        IFieldSymbol field,
        string propertyName,
        PropertyDeclarationSyntax property,
        int externalRefCount,
        bool fieldRenamed)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = DescribeReferenceUpdates(
                    @params.FieldName!,
                    propertyName,
                    @params.UpdateReferences,
                    externalRefCount,
                    fieldRenamed),
                BeforeSnippet = $"{field.DeclaredAccessibility.ToString().ToLower()} {field.Type.ToDisplayString()} {@params.FieldName};",
                AfterSnippet = $"private {field.Type.ToDisplayString()} {@params.FieldName};\n\n{property.NormalizeWhitespace()}"
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    internal static string DescribeReferenceUpdates(
        string fieldName,
        string propertyName,
        bool updateReferences,
        int externalRefCount,
        bool fieldRenamed = false)
    {
        var referenceClause = updateReferences
            ? $"{externalRefCount} external references to update"
            : fieldRenamed
                ? $"{externalRefCount} external references will follow the renamed backing field"
                : "external references will not be updated";
        return $"Encapsulate field '{fieldName}' as property '{propertyName}' ({referenceClause})";
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

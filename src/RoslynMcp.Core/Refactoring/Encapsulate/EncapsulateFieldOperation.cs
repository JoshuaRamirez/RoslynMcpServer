using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
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

            // When processing all files, sourceFile is optional; validate path
            // shape only (filter throws SourceFileNotFound / SourceNotInWorkspace).
            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.FieldName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "fieldName is required.");

        if (!SyntaxIdentifierValidation.IsValidIdentifier(@params.FieldName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid field name: {@params.FieldName}");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.PropertyName != null && !SyntaxIdentifierValidation.IsValidIdentifier(@params.PropertyName))
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

        // A field named only underscores (e.g. `_`, `@_`, `__`) derives an
        // empty name; reject rather than generate a property with no identifier.
        if (propertyName.Length == 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolName,
                $"Cannot derive a property name from field '{fieldName}'; specify propertyName.");
        }

        // Check for an existing member with the same name (property,
        // field, method, …, including other partial declarations),
        // excluding the field being encapsulated, as the all-files path
        // does. Compare the unescaped name so `@foo` collides with `foo`.
        var containingTypeSymbol = fieldSymbol.ContainingType;
        var existingMember = containingTypeSymbol
            .GetMembers(SyntaxIdentifierValidation.NormalizeIdentifier(propertyName))
            .FirstOrDefault(member => !SymbolEqualityComparer.Default.Equals(member, fieldSymbol));

        if (existingMember != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                existingMember is IPropertySymbol
                    ? $"Property '{propertyName}' already exists."
                    : $"Member '{propertyName}' already exists.");
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

        var referenceLocations = references
            .SelectMany(r => r.Locations)
            .ToList();
        var externalReferences = referenceLocations
            .Where(loc => !IsInsideContainingType(loc, fieldSymbol.ContainingType))
            .ToList();
        var internalReferences = referenceLocations
            .Where(loc => IsInsideContainingType(loc, fieldSymbol.ContainingType))
            .ToList();

        // Rename field if property name would conflict (computed before preview
        // so the description can mention backing-field rename updates).
        var newFieldName = fieldName;
        var fieldRenamed = false;
        if (PropertyNameConflictsWithField(propertyName, fieldName))
        {
            newFieldName = DeriveBackingFieldName(fieldName);
            fieldRenamed = true;
            if (containingTypeSymbol.GetMembers(newFieldName).Any())
            {
                throw new RefactoringException(
                    ErrorCodes.NameCollision,
                    $"Backing field name '{newFieldName}' already exists.");
            }
        }

        // A rewritten use must not be captured by a local or parameter that
        // already has the new name (silent rebinding).
        var shadowed = await FindShadowedRewriteAsync(
            PlannedReferenceRewrites(externalReferences, internalReferences, propertyName, newFieldName, fieldRenamed, @params.UpdateReferences),
            cancellationToken);
        if (shadowed is { } hit)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Cannot rewrite references to '{hit.Name}': a local, parameter or lambda parameter named '{hit.Name}' is already in scope at {Path.GetFileName(hit.FilePath)}:{hit.Line}, so the use would bind to it instead.");
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
            internalReferences,
            newFieldName,
            fieldRenamed,
            @params.UpdateReferences,
            cancellationToken);

        // If preview mode, return without applying. The snippets come from the source declaration and the
        // computed result, so they show real modifiers and the renamed backing field (#3066).
        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                fieldDeclaration,
                newSolution.GetDocument(document.Id),
                fieldSymbol.ContainingType.Name,
                newFieldName,
                propertyName,
                externalReferences.Count,
                fieldRenamed,
                cancellationToken);
        }

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
    /// as <see cref="FindFieldDeclarator"/>; locals excluded). Optional
    /// <c>sourceFile</c> limits the <em>field-selection walk</em> via
    /// <see cref="AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile"/>
    /// (default path; same as FormatDocument / SortUsings / AddMissingUsings /
    /// RemoveUnusedUsings / simplify_name / add_braces / remove_braces /
    /// convert_expression_body / invert_if / convert_foreach_linq /
    /// convert_property / convert_to_async / convert_to_pattern_matching /
    /// convert_to_interpolated_string). The unfiltered document list is
    /// preserved for PendingChanges / preview so cross-file reference
    /// rewrites under <c>updateReferences: true</c> still appear even when
    /// <c>sourceFile</c> scoped the walk (same as InlineMethod / InlineConstant /
    /// InlineVariable / ConvertToAsync). Const fields, fields whose derived
    /// property name already exists, and other per-field failures are
    /// skipped. Property names are derived per field via
    /// <see cref="DerivePropertyName"/>. When every file is a no-op,
    /// succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        EncapsulateFieldParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);
        // Optional sourceFile limits which docs are scanned for fields to
        // encapsulate; keep the full list for documentsToCompare so
        // updateReferences cross-file callers still appear in preview /
        // PendingChanges (peer InlineMethod / InlineConstant / ConvertToAsync).
        var documentsToWalk = allDocuments;
        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            documentsToWalk = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(
                allDocuments, @params.SourceFile!);
        }

        var encapsulatedCountByDoc = new Dictionary<DocumentId, int>();

        foreach (var document in documentsToWalk)
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

        var documentsToCompare = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

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
        if (propertyName.Length == 0)
            return null;

        var containingType = fieldDeclarator.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return null;

        // Skip when the derived name is already a member of this type
        // (property, field, method, …), including other partial
        // declarations. Do not treat the field being encapsulated as a
        // collision with itself (e.g. field `Name` renamed to `_name`).
        var containingTypeSymbol = fieldSymbol.ContainingType;
        if (containingTypeSymbol != null &&
            containingTypeSymbol.GetMembers(propertyName)
                .Any(member => !SymbolEqualityComparer.Default.Equals(member, fieldSymbol)))
        {
            return null;
        }

        var newFieldName = fieldName;
        var fieldRenamed = false;
        if (PropertyNameConflictsWithField(propertyName, fieldName))
        {
            newFieldName = DeriveBackingFieldName(fieldName);
            fieldRenamed = true;
            if (containingTypeSymbol != null &&
                containingTypeSymbol.GetMembers(newFieldName).Any())
            {
                return null;
            }
        }

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

        var referenceLocations = references
            .SelectMany(r => r.Locations)
            .ToList();
        var externalReferences = referenceLocations
            .Where(loc => !IsInsideContainingType(loc, fieldSymbol.ContainingType))
            .ToList();
        var internalReferences = referenceLocations
            .Where(loc => IsInsideContainingType(loc, fieldSymbol.ContainingType))
            .ToList();

        var shadowed = await FindShadowedRewriteAsync(
            PlannedReferenceRewrites(externalReferences, internalReferences, propertyName, newFieldName, fieldRenamed, updateReferences),
            cancellationToken);
        if (shadowed != null)
            return null;

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
            internalReferences,
            newFieldName,
            fieldRenamed,
            updateReferences,
            cancellationToken);
    }

    /// <summary>
    /// Returns the first rewritten reference whose new simple name would bind
    /// to a local, parameter, lambda parameter, range variable or local
    /// function already in scope there, instead of the field or property
    /// (<c>int _foo = 100; return _foo + foo;</c> would silently become
    /// <c>_foo + _foo</c>). Qualified uses (<c>this.foo</c>, <c>obj.foo</c>,
    /// <c>?.foo</c>) cannot be captured and are skipped. Returns null when
    /// every rewrite is safe.
    /// </summary>
    internal static async Task<(string Name, string FilePath, int Line)?> FindShadowedRewriteAsync(
        IEnumerable<(ReferenceLocation Reference, string Replacement)> rewrites,
        CancellationToken cancellationToken)
    {
        var semanticModels = new Dictionary<DocumentId, SemanticModel?>();
        foreach (var (reference, replacement) in rewrites)
        {
            var document = reference.Document;
            if (!semanticModels.TryGetValue(document.Id, out var semanticModel))
            {
                semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                semanticModels[document.Id] = semanticModel;
            }

            if (semanticModel == null)
                continue;

            var root = await semanticModel.SyntaxTree.GetRootAsync(cancellationToken);
            if (root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true)
                is not IdentifierNameSyntax identifier)
            {
                continue;
            }

            if (identifier.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifier ||
                identifier.Parent is MemberBindingExpressionSyntax ||
                identifier.Parent is QualifiedNameSyntax qualified && qualified.Right == identifier)
            {
                continue;
            }

            var name = SyntaxIdentifierValidation.NormalizeIdentifier(replacement);
            var shadowing = semanticModel.LookupSymbols(identifier.SpanStart, name: name)
                .Any(symbol => symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol ||
                               symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction });
            if (shadowing)
            {
                var line = reference.Location.GetLineSpan().StartLinePosition.Line + 1;
                return (name, document.FilePath ?? document.Name, line);
            }
        }

        return null;
    }

    /// <summary>
    /// The (reference, new name) pairs <see cref="ApplyEncapsulateToSolutionAsync"/>
    /// will write: external uses follow the property (or the renamed field when
    /// references are not redirected), same-type uses follow a renamed field.
    /// </summary>
    private static IEnumerable<(ReferenceLocation Reference, string Replacement)> PlannedReferenceRewrites(
        IReadOnlyList<ReferenceLocation> externalReferences,
        IReadOnlyList<ReferenceLocation> internalReferences,
        string propertyName,
        string newFieldName,
        bool fieldRenamed,
        bool updateReferences)
    {
        var externalReplacement = updateReferences
            ? propertyName
            : fieldRenamed ? newFieldName : null;
        if (externalReplacement != null)
        {
            foreach (var reference in externalReferences)
                yield return (reference, externalReplacement);
        }

        if (fieldRenamed)
        {
            foreach (var reference in internalReferences)
                yield return (reference, newFieldName);
        }
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
        IReadOnlyList<ReferenceLocation> internalReferences,
        string newFieldName,
        bool fieldRenamed,
        bool updateReferences,
        CancellationToken cancellationToken)
    {
        // A renamed backing field (`@foo` -> `_foo`) renames the selected
        // declarator and carries uses inside the same declaration along
        // (`static int @foo = 1, copy = foo;` -> `copy = _foo`), because
        // the same-type reference rewrite below skips the field
        // declaration it replaces wholesale.
        var newFieldDeclaration = fieldDeclaration;
        if (fieldRenamed)
        {
            var declarationIdentifiers = EnumerateSameFileIdentifiers(
                    root, document.Id, internalReferences, fieldName)
                .Where(identifier => fieldDeclaration.Span.Contains(identifier.Span))
                .ToList();
            newFieldDeclaration = fieldDeclaration.ReplaceNodes(
                declarationIdentifiers.Cast<SyntaxNode>().Append(fieldDeclarator),
                (original, rewritten) => original == fieldDeclarator
                    ? ((VariableDeclaratorSyntax)rewritten).WithIdentifier(SyntaxFactory.Identifier(newFieldName))
                    : SyntaxFactory.IdentifierName(newFieldName).WithTriviaFrom(original));

            // Update property to use new field name
            property = UpdatePropertyFieldName(property, newFieldName);
        }

        // Make field private if it's not already
        if (fieldSymbol.DeclaredAccessibility != Accessibility.Private)
        {
            var newModifiers = SyntaxFactory.TokenList(
                fieldDeclaration.Modifiers.Where(m =>
                    !m.IsKind(SyntaxKind.PublicKeyword) &&
                    !m.IsKind(SyntaxKind.ProtectedKeyword) &&
                    !m.IsKind(SyntaxKind.InternalKeyword))
                .Prepend(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

            newFieldDeclaration = newFieldDeclaration.WithModifiers(newModifiers);
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
        var internalRefAnnotation = new SyntaxAnnotation("encapsulate-field-internal-ref");
        newFieldDeclaration = newFieldDeclaration.WithAdditionalAnnotations(fieldAnnotation);

        var replacements = new Dictionary<SyntaxNode, SyntaxNode> { [fieldDeclaration] = newFieldDeclaration };
        if (referenceReplacement != null)
        {
            foreach (var identifier in EnumerateSameFileIdentifiers(
                         root, document.Id, externalReferences, fieldName))
            {
                replacements[identifier] = identifier.WithAdditionalAnnotations(refAnnotation);
            }
        }

        // A renamed backing field (`foo` / `@foo` -> `_foo`) must also
        // carry same-type uses along, otherwise `Display() => foo` binds to
        // a name that no longer exists.
        if (fieldRenamed)
        {
            foreach (var identifier in EnumerateSameFileIdentifiers(
                         root, document.Id, internalReferences, fieldName))
            {
                if (fieldDeclaration.Span.Contains(identifier.Span))
                    continue;
                replacements[identifier] = identifier.WithAdditionalAnnotations(internalRefAnnotation);
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

        if (fieldRenamed)
        {
            var annotatedInternalRefs = newRoot.GetAnnotatedNodes(internalRefAnnotation)
                .OfType<IdentifierNameSyntax>()
                .ToList();
            if (annotatedInternalRefs.Count > 0)
            {
                newRoot = newRoot.ReplaceNodes(
                    annotatedInternalRefs,
                    (original, _) => SyntaxFactory.IdentifierName(newFieldName)
                        .WithTriviaFrom(original));
            }
        }

        var stillAnnotated = newRoot.GetAnnotatedNodes(fieldAnnotation).FirstOrDefault();
        if (stillAnnotated != null)
            newRoot = newRoot.ReplaceNode(stillAnnotated, stillAnnotated.WithoutAnnotations(fieldAnnotation));

        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // Cross-file rewrites: external references follow
        // referenceReplacement; same-type references in other partial
        // declarations follow a renamed backing field.
        var crossFileRewrites = new List<(ReferenceLocation Reference, string Replacement)>();
        if (referenceReplacement != null)
            crossFileRewrites.AddRange(externalReferences.Select(r => (r, referenceReplacement)));
        if (fieldRenamed)
            crossFileRewrites.AddRange(internalReferences.Select(r => (r, newFieldName)));

        if (crossFileRewrites.Count > 0)
        {
            // Group by document and rewrite from the pre-replacement root
            // so later SourceSpans stay valid after the first identifier
            // in that file is replaced.
            foreach (var group in crossFileRewrites
                .Where(rewrite => rewrite.Reference.Document.Id != document.Id)
                .GroupBy(rewrite => rewrite.Reference.Document.Id))
            {
                var refDoc = newSolution.GetDocument(group.Key);
                if (refDoc == null) continue;

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null) continue;

                var identifiers = new Dictionary<IdentifierNameSyntax, string>();
                foreach (var (reference, replacement) in group)
                {
                    var refNode = refRoot.FindNode(
                        reference.Location.SourceSpan, getInnermostNodeForTie: true);
                    if (refNode is IdentifierNameSyntax identifier &&
                        IdentifierMatchesField(identifier, fieldName))
                    {
                        identifiers.TryAdd(identifier, replacement);
                    }
                }

                if (identifiers.Count == 0)
                    continue;

                var newRefRoot = refRoot.ReplaceNodes(
                    identifiers.Keys,
                    (original, _) => SyntaxFactory.IdentifierName(identifiers[original])
                        .WithTriviaFrom(original));
                newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
            }
        }

        return newSolution;
    }

    /// <summary>
    /// Finds a field by <paramref name="fieldName"/>. Omitted
    /// <paramref name="column"/> keeps today's fieldName + optional
    /// <paramref name="line"/> pick, including omitted-line field
    /// <c>VariableDeclaratorSyntax</c> <c>FirstOrDefault</c> and
    /// line-only exclusive-end coverage (<see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
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
                .Where(declarator => FieldCoverage.FieldCoversColumn(declarator, line!.Value, column.Value))
                .OrderBy(declarator => FieldCoverage.IdentifierCoversColumn(declarator, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(declarator => FieldCoverage.SmallestCoveringSpanLength(declarator, line!.Value, column.Value))
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return matches.FirstOrDefault();

        return matches
            .Where(declarator => FieldCoverage.FieldCoversLine(declarator, line.Value))
            .OrderBy(declarator => FieldCoverage.IdentifierCoversLine(declarator, line.Value) ? 0 : 1)
            .ThenBy(declarator => FieldCoverage.SmallestCoveringSpanLength(declarator, line.Value))
            .FirstOrDefault();

        bool IsMatchingFieldDeclarator(VariableDeclaratorSyntax declarator) =>
            declarator.Identifier.Text == fieldName &&
            IsFieldDeclarator(declarator);
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

    private static IEnumerable<IdentifierNameSyntax> EnumerateSameFileIdentifiers(
        SyntaxNode root,
        DocumentId documentId,
        IEnumerable<ReferenceLocation> references,
        string fieldName)
    {
        var seen = new HashSet<IdentifierNameSyntax>();
        foreach (var reference in references)
        {
            if (reference.Document.Id != documentId)
                continue;

            var refNode = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
            if (refNode is IdentifierNameSyntax identifier &&
                IdentifierMatchesField(identifier, fieldName) &&
                seen.Add(identifier))
            {
                yield return identifier;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="identifier"/> names <paramref name="fieldName"/>,
    /// comparing unescaped values so a verbatim declaration (<c>@foo</c>)
    /// matches a plain use (<c>foo</c>) and vice versa.
    /// </summary>
    private static bool IdentifierMatchesField(IdentifierNameSyntax identifier, string fieldName) =>
        identifier.Identifier.ValueText == SyntaxIdentifierValidation.NormalizeIdentifier(fieldName);

    /// <summary>
    /// Derives a property name from <paramref name="fieldName"/> by
    /// stripping a leading underscore and capitalizing the first letter.
    /// </summary>
    internal static string DerivePropertyName(string fieldName)
    {
        // Strip a verbatim '@' (e.g. '@class' -> 'class') before deriving
        // so the result is a plain identifier ('Class'), then remove a
        // leading underscore if present.
        var name = SyntaxIdentifierValidation.NormalizeIdentifier(fieldName).TrimStart('_');

        // Capitalize first letter
        if (name.Length > 0)
        {
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        return name;
    }

    /// <summary>
    /// True when <paramref name="propertyName"/> and <paramref name="fieldName"/>
    /// differ only by case or a verbatim <c>@</c> prefix, so the backing
    /// field must be renamed.
    /// </summary>
    internal static bool PropertyNameConflictsWithField(string propertyName, string fieldName) =>
        SyntaxIdentifierValidation.NormalizeIdentifier(propertyName).Equals(
            SyntaxIdentifierValidation.NormalizeIdentifier(fieldName),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives the renamed backing field name (<c>_camelCase</c>) from
    /// <paramref name="fieldName"/>, stripping a verbatim <c>@</c> first so
    /// <c>@class</c> becomes <c>_class</c> rather than the invalid <c>_@class</c>.
    /// </summary>
    internal static string DeriveBackingFieldName(string fieldName)
    {
        var bare = SyntaxIdentifierValidation.NormalizeIdentifier(fieldName);
        return "_" + char.ToLowerInvariant(bare[0]) + bare.Substring(1);
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

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        EncapsulateFieldParams @params,
        FieldDeclarationSyntax fieldDeclaration,
        Document? newDocument,
        string containingTypeName,
        string newFieldName,
        string propertyName,
        int externalRefCount,
        bool fieldRenamed,
        CancellationToken cancellationToken)
    {
        var afterParts = new List<string>();
        if (newDocument != null && await newDocument.GetSyntaxRootAsync(cancellationToken) is { } newRoot)
        {
            var containingType = newRoot.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.ValueText == containingTypeName &&
                                     t.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == propertyName));
            if (containingType != null)
            {
                afterParts.AddRange(containingType.Members
                    .OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == newFieldName))
                    .Select(f => f.ToString()));
                afterParts.AddRange(containingType.Members
                    .OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Identifier.ValueText == propertyName)
                    .Select(p => p.ToString()));
            }
        }

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
                BeforeSnippet = fieldDeclaration.ToString(),
                AfterSnippet = string.Join("\n\n", afterParts)
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

}

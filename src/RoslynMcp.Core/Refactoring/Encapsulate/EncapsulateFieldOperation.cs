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
/// Honors optional <c>line</c> to disambiguate same-named fields in one
/// file (identifier preferred, then smallest covering declarator/field).
/// Omitted line keeps today's field <c>VariableDeclaratorSyntax</c>
/// <c>FirstOrDefault</c> pick. Locals and other non-field declarators
/// stay excluded. Nested types participate when line is set. If line is
/// set and no covering field matches, throw <c>FieldNotFound</c> rather
/// than falling back to first-match. After the rewrite rematches the
/// selected field, recover it from a per-execution syntax annotation
/// and strip the annotation before commit. Same-file outer/sibling
/// references are external (semantic containment in the selected type,
/// not file-path equality) and are annotated before the field rewrite.
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
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Optional line disambiguates same-named fields. Omitted keeps
        // today's field VariableDeclaratorSyntax FirstOrDefault. Line
        // set picks the covering field (identifier preferred, then
        // smallest covering declarator/field). Nested types participate.
        // Locals stay excluded. Do not fall back to first-match when
        // line is set and nothing covers that line.
        var fieldDeclarator = FindFieldDeclarator(root, @params.FieldName, @params.Line);
        if (fieldDeclarator == null)
        {
            throw new RefactoringException(
                ErrorCodes.FieldNotFound,
                $"Field '{@params.FieldName}' not found.");
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
        var propertyName = @params.PropertyName ?? DerivePropertyName(@params.FieldName);

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
        var newFieldName = @params.FieldName;
        var fieldRenamed = false;
        if (propertyName.Equals(@params.FieldName, StringComparison.OrdinalIgnoreCase))
        {
            newFieldName = "_" + char.ToLowerInvariant(@params.FieldName[0]) + @params.FieldName.Substring(1);
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
        var referenceReplacement = @params.UpdateReferences
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
                         root, document.Id, externalReferences, @params.FieldName))
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
                    identifier.Identifier.Text == @params.FieldName)
                {
                    var newIdentifier = SyntaxFactory.IdentifierName(referenceReplacement)
                        .WithTriviaFrom(identifier);
                    var newRefRoot = refRoot.ReplaceNode(identifier, newIdentifier);
                    newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
                }
            }
        }

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
    /// Finds a field by <paramref name="fieldName"/>. Omitted
    /// <paramref name="line"/> keeps today's field
    /// <c>VariableDeclaratorSyntax</c> <c>FirstOrDefault</c> pick,
    /// including when several same-named fields exist (nested vs outer).
    /// Locals and other non-field declarators stay excluded. When set,
    /// picks the field whose identifier or declaration span covers that
    /// 1-based line (same exclusive-end coverage as
    /// <c>UseBaseTypeOperation.SpanCoversLine</c> /
    /// <c>PullMembersUpOperation.SpanCoversLine</c> /
    /// <c>PushMembersDownOperation.SpanCoversLine</c>). Prefer the
    /// identifier hit, then the smallest covering declarator or field.
    /// Nested types participate. Do not require the declaration to start
    /// on <paramref name="line"/> — a split declaration may put the
    /// identifier on a continuation line. If nothing covers this line,
    /// return null rather than falling back to first-match. After the
    /// rewrite rematches the selected field, recover it from the
    /// per-execution syntax annotation — do not reuse a pre-rewrite
    /// SpanStart or line.
    /// </summary>
    internal static VariableDeclaratorSyntax? FindFieldDeclarator(
        SyntaxNode root,
        string fieldName,
        int? line)
    {
        var matches = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(IsMatchingFieldDeclarator)
            .ToList();

        if (!line.HasValue)
            return matches.FirstOrDefault();

        return matches
            .Where(declarator => FieldCoversLine(declarator, line.Value))
            .OrderBy(declarator => IdentifierCoversLine(declarator, line.Value) ? 0 : 1)
            .ThenBy(declarator => SmallestCoveringSpanLength(declarator, line.Value))
            .FirstOrDefault();

        bool IsMatchingFieldDeclarator(VariableDeclaratorSyntax declarator) =>
            declarator.Identifier.Text == fieldName &&
            declarator.Parent?.Parent is FieldDeclarationSyntax;
    }

    private static bool FieldCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        IdentifierCoversLine(declarator, line) ||
        SpanCoversLine(declarator.GetLocation().GetLineSpan(), line) ||
        (GetFieldDeclaration(declarator) is { } field &&
         SpanCoversLine(field.GetLocation().GetLineSpan(), line));

    private static bool IdentifierCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        SpanCoversLine(declarator.Identifier.GetLocation().GetLineSpan(), line);

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

    private static FieldDeclarationSyntax? GetFieldDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent as FieldDeclarationSyntax;

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

    private static string DerivePropertyName(string fieldName)
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
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = DescribeReferenceUpdates(
                    @params.FieldName,
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

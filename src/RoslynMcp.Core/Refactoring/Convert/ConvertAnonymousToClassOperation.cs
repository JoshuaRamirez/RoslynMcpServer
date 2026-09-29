using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using SymbolKind = RoslynMcp.Contracts.Enums.SymbolKind;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts an anonymous type (<c>new { ... }</c>) to a named class or record
/// and replaces same-shape anonymous creations that share that constructed type.
/// </summary>
public sealed class ConvertAnonymousToClassOperation : RefactoringOperationBase<ConvertAnonymousToClassParams>
{
    /// <summary>
    /// Creates a new convert-anonymous-to-class operation.
    /// </summary>
    public ConvertAnonymousToClassOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertAnonymousToClassParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-anonymous-to-class inputs. Internal so tests can exercise
    /// rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertAnonymousToClassParams @params)
    {
        if (@params.AllFiles)
        {
            if (@params.Line.HasValue || @params.Column.HasValue || !string.IsNullOrWhiteSpace(@params.NewTypeName))
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with line, column, or newTypeName.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.NewTypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newTypeName is required.");

        if (!@params.Line.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "line is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (@params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!SyntaxIdentifierValidation.IsValidIdentifier(@params.NewTypeName!))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolName,
                $"'{@params.NewTypeName}' is not a valid C# type name.");
        }
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
        ConvertAnonymousToClassParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var creation = FindAnonymousCreation(root, @params.Line!.Value, @params.Column);
        var anonymousType = GetAnonymousType(semanticModel, creation);
        var members = GetAnonymousMembers(anonymousType);
        var targetNamespace = NamespaceNameHelpers.GetContainingNamespaceName(semanticModel, creation);

        await ValidateNoNameConflictAsync(document, @params.NewTypeName!, targetNamespace, cancellationToken);

        var creations = await CollectSameShapeCreationsAsync(
            document.Project,
            anonymousType,
            cancellationToken);

        if (creations.Count == 0)
            creations.Add(new CreationTarget(document, creation.Span));

        foreach (var target in creations)
            DocumentEditableHelpers.ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var insertPosition = TypeInsertionHelpers.GetTypeInsertionPosition(root, creation);
        ValidateMembersForGeneratedType(members, semanticModel, insertPosition);
        var typeDeclaration = CreateNamedType(
            @params.NewTypeName!,
            @params.AsRecord,
            members,
            semanticModel,
            insertPosition);

        var newSolution = await ApplyChangesAsync(
            document,
            typeDeclaration,
            creations,
            members,
            @params.NewTypeName!,
            targetNamespace,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                cancellationToken);
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);
        var qualifiedName = string.IsNullOrEmpty(targetNamespace)
            ? @params.NewTypeName!
            : $"{targetNamespace}.{@params.NewTypeName}";

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
                Name = @params.NewTypeName!,
                FullyQualifiedName = qualifiedName,
                Kind = @params.AsRecord ? SymbolKind.Record : SymbolKind.Class
            },
            creations.Count,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>RenameSymbolOperation.ExecuteAllFilesAsync</c> /
    /// <c>RenameNamespaceOperation.ExecuteAllFilesAsync</c>) and converts every
    /// distinct eligible anonymous-type shape, naming each type from sanitized
    /// property names (PascalCase join; <c>@</c>-escape reserved keywords when
    /// fixable; numeric suffix on collision). Optional <c>sourceFile</c> limits
    /// via <see cref="AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile"/>.
    /// Linked multi-project views of the same path are skipped rather than
    /// coalescing (same contract as <c>SafeDeleteOperation.ExecuteAllFilesAsync</c> /
    /// rename_symbol / rename_namespace allFiles). Empty/invalid derived names,
    /// unfixable collisions, uneditable / source-generated docs, members that
    /// fail today's generated-type validation, and otherwise ineligible sites
    /// are skipped rather than failing the walk. Deterministic document
    /// <c>FilePath</c> then creation <c>SpanStart</c> order; each distinct shape
    /// once per project. When every file is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ConvertAnonymousToClassParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            allDocuments = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(
                allDocuments, @params.SourceFile!);
        }

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);
        var linkedPathCounts = AllFilesDocumentHelpers.BuildLinkedPathCounts(originalSolution);
        var convertedShapes = new HashSet<(ProjectId ProjectId, string ShapeKey)>();
        var changedCountByDoc = new Dictionary<DocumentId, int>();

        bool madeProgress;
        do
        {
            madeProgress = false;

            foreach (var linkedDocuments in documentGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (linkedDocuments.Count > 1)
                    continue;

                var primary = linkedDocuments.FirstOrDefault(d =>
                    d is not SourceGeneratedDocument &&
                    DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace));
                if (primary == null)
                    continue;

                while (true)
                {
                    Context.UpdateSolution(currentSolution);

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
                    foreach (var creation in CollectAnonymousCreations(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            updated = await TryConvertOneAllFilesAsync(
                                currentDocument,
                                semanticModel,
                                creation,
                                @params.AsRecord,
                                convertedShapes,
                                linkedPathCounts,
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
                    currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                        beforeSolution,
                        updated,
                        Context.Workspace,
                        cancellationToken);
                    Context.UpdateSolution(currentSolution);

                    changedCountByDoc[primary.Id] =
                        changedCountByDoc.GetValueOrDefault(primary.Id) + 1;
                    madeProgress = true;
                }
            }
        } while (madeProgress);

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

                var changedCount = changedCountByDoc.GetValueOrDefault(document.Id);
                if (changedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        changedCount = Math.Max(changedCount, changedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = changedCount > 0
                        ? BuildAllFilesDescription(changedCount, @params.AsRecord)
                        : $"Convert anonymous type to {(@params.AsRecord ? "record" : "class")}"
                });
                continue;
            }

            anyChanged = true;
        }

        if (@params.Preview)
        {
            Context.UpdateSolution(originalSolution);
            return RefactoringResult.PreviewResult(operationId, allPendingChanges);
        }

        Context.UpdateSolution(originalSolution);

        if (!anyChanged)
        {
            return RefactoringResult.Succeeded(operationId,
                new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
                null, 0, 0);
        }

        var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            null, 0, 0);
    }

    /// <summary>
    /// Preview description for a file that converted
    /// <paramref name="convertedCount"/> anonymous shapes.
    /// </summary>
    internal static string BuildAllFilesDescription(int convertedCount, bool asRecord)
    {
        var kind = asRecord ? "record" : "class";
        return convertedCount == 1
            ? $"Convert anonymous type to {kind}"
            : $"Convert {convertedCount} anonymous types to {kind}";
    }

    /// <summary>
    /// Anonymous object creations in <paramref name="root"/>, deterministic
    /// <c>SpanStart</c> then span-length order.
    /// </summary>
    internal static IReadOnlyList<AnonymousObjectCreationExpressionSyntax> CollectAnonymousCreations(
        SyntaxNode root)
    {
        return root.DescendantNodes()
            .OfType<AnonymousObjectCreationExpressionSyntax>()
            .OrderBy(n => n.SpanStart)
            .ThenBy(n => n.Span.Length)
            .ToList();
    }

    /// <summary>
    /// Derives a PascalCase type name by joining sanitized property names.
    /// Returns <see langword="null"/> when empty, invalid, or an unfixable keyword.
    /// </summary>
    internal static string? DeriveTypeNameFromMembers(IReadOnlyList<AnonymousMember> members)
    {
        if (members.Count == 0)
            return null;

        var builder = new System.Text.StringBuilder();
        foreach (var member in members)
        {
            var bare = SyntaxIdentifierValidation.NormalizeIdentifier(member.Name);
            if (string.IsNullOrEmpty(bare))
                return null;

            builder.Append(char.ToUpperInvariant(bare[0]));
            if (bare.Length > 1)
                builder.Append(bare[1..]);
        }

        return FinalizeTypeName(builder.ToString());
    }

    /// <summary>
    /// Appends a numeric suffix (<paramref name="suffix"/> ≥ 2) to
    /// <paramref name="typeName"/>, re-finalizing for keyword escaping.
    /// </summary>
    internal static string? WithNumericSuffix(string typeName, int suffix)
    {
        if (suffix < 2)
            return FinalizeTypeName(typeName);

        var bare = SyntaxIdentifierValidation.NormalizeIdentifier(typeName);
        return FinalizeTypeName(bare + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Stable shape key from member names and fully-qualified type display strings.
    /// </summary>
    internal static string BuildShapeKey(IReadOnlyList<AnonymousMember> members)
    {
        return string.Join(
            "\u001f",
            members.Select(m =>
                m.Name + "\u001e" + m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
    }

    private static string? FinalizeTypeName(string? seed)
    {
        if (string.IsNullOrEmpty(seed))
            return null;

        var name = seed;
        if (char.IsDigit(name[0]))
            name = "T" + name;

        if (char.IsLower(name[0]))
            name = char.ToUpperInvariant(name[0]) + name[1..];

        if (!SyntaxIdentifierValidation.IsValidIdentifier(name))
        {
            var keywordKind = SyntaxFacts.GetKeywordKind(name);
            if (keywordKind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(keywordKind))
                name = "@" + name;
            else
                return null;
        }

        return SyntaxIdentifierValidation.IsValidIdentifier(name) ? name : null;
    }

    private async Task<Solution?> TryConvertOneAllFilesAsync(
        Document document,
        SemanticModel semanticModel,
        AnonymousObjectCreationExpressionSyntax creation,
        bool asRecord,
        HashSet<(ProjectId ProjectId, string ShapeKey)> convertedShapes,
        IReadOnlyDictionary<string, int> linkedPathCounts,
        CancellationToken cancellationToken)
    {
        if (AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(document, linkedPathCounts))
            return null;

        INamedTypeSymbol anonymousType;
        try
        {
            anonymousType = GetAnonymousType(semanticModel, creation);
        }
        catch (RefactoringException)
        {
            return null;
        }

        var members = GetAnonymousMembers(anonymousType);
        var shapeKey = BuildShapeKey(members);
        var shapeId = (document.Project.Id, shapeKey);
        if (convertedShapes.Contains(shapeId))
            return null;

        var baseName = DeriveTypeNameFromMembers(members);
        if (baseName == null)
        {
            convertedShapes.Add(shapeId);
            return null;
        }

        var targetNamespace = NamespaceNameHelpers.GetContainingNamespaceName(semanticModel, creation);
        string? chosenName = null;
        for (var suffix = 0; suffix < 100; suffix++)
        {
            var candidate = suffix == 0 ? baseName : WithNumericSuffix(baseName, suffix + 1);
            if (candidate == null)
                continue;

            try
            {
                await ValidateNoNameConflictAsync(document, candidate, targetNamespace, cancellationToken);
                chosenName = candidate;
                break;
            }
            catch (RefactoringException)
            {
                // try next suffix
            }
        }

        if (chosenName == null)
        {
            convertedShapes.Add(shapeId);
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            convertedShapes.Add(shapeId);
            return null;
        }

        var insertPosition = TypeInsertionHelpers.GetTypeInsertionPosition(root, creation);
        try
        {
            ValidateMembersForGeneratedType(members, semanticModel, insertPosition);
        }
        catch (RefactoringException)
        {
            convertedShapes.Add(shapeId);
            return null;
        }

        List<CreationTarget> creations;
        try
        {
            creations = await CollectSameShapeCreationsAsync(
                document.Project,
                anonymousType,
                cancellationToken);
        }
        catch (RefactoringException)
        {
            convertedShapes.Add(shapeId);
            return null;
        }

        if (creations.Count == 0)
            creations.Add(new CreationTarget(document, creation.Span));

        foreach (var target in creations)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(target.Document, Context.Workspace) ||
                target.Document is SourceGeneratedDocument ||
                AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(target.Document, linkedPathCounts))
            {
                convertedShapes.Add(shapeId);
                return null;
            }
        }

        var typeDeclaration = CreateNamedType(
            chosenName,
            asRecord,
            members,
            semanticModel,
            insertPosition);

        Solution newSolution;
        try
        {
            newSolution = await ApplyChangesAsync(
                document,
                typeDeclaration,
                creations,
                members,
                chosenName,
                targetNamespace,
                cancellationToken);
        }
        catch (RefactoringException)
        {
            convertedShapes.Add(shapeId);
            return null;
        }

        convertedShapes.Add(shapeId);
        return newSolution;
    }

    internal static AnonymousObjectCreationExpressionSyntax FindAnonymousCreation(
        SyntaxNode root,
        ConvertAnonymousToClassParams @params)
    {
        if (!@params.Line.HasValue)
        {
            throw new RefactoringException(
                ErrorCodes.MissingRequiredParam,
                "line is required.");
        }

        return FindAnonymousCreation(root, @params.Line.Value, @params.Column);
    }

    internal static AnonymousObjectCreationExpressionSyntax FindAnonymousCreation(
        SyntaxNode root,
        int line,
        int? column)
    {
        var candidates = root.DescendantNodes()
            .OfType<AnonymousObjectCreationExpressionSyntax>()
            .Where(n => SpanCoverage.SpanCoversLine(n.GetLocation().GetLineSpan(), line, column))
            .ToList();

        if (candidates.Count == 1)
            return candidates[0];

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"No anonymous type found at line {line}.");
        }

        if (column.HasValue)
        {
            var atColumn = candidates
                .Where(n => SpanCoverage.SpanCoversColumn(n.GetLocation().GetLineSpan(), line, column.Value))
                .ToList();
            if (atColumn.Count == 1)
                return atColumn[0];
        }

        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple anonymous object creations found at line {line}. Provide column.");
    }

    internal static INamedTypeSymbol GetAnonymousType(
        SemanticModel semanticModel,
        AnonymousObjectCreationExpressionSyntax creation)
    {
        var type = semanticModel.GetTypeInfo(creation).Type as INamedTypeSymbol;
        if (type == null || !type.IsAnonymousType)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "The selected expression is not an anonymous type.");
        }

        return type;
    }

    internal static IReadOnlyList<AnonymousMember> GetAnonymousMembers(INamedTypeSymbol anonymousType)
    {
        var ctor = anonymousType.InstanceConstructors
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        if (ctor is { Parameters.Length: > 0 })
        {
            return ctor.Parameters
                .Select(p => new AnonymousMember(p.Name, p.Type))
                .ToList();
        }

        return anonymousType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
            .Select(p => new AnonymousMember(p.Name, p.Type))
            .ToList();
    }

    internal static bool SharesAnonymousType(ITypeSymbol? candidate, INamedTypeSymbol original)
    {
        if (candidate is not INamedTypeSymbol named || !named.IsAnonymousType)
            return false;

        if (SymbolEqualityComparer.Default.Equals(named, original))
            return true;

        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, original.ContainingAssembly))
            return false;

        return MembersMatch(GetAnonymousMembers(named), GetAnonymousMembers(original));
    }

    internal static TypeDeclarationSyntax CreateNamedType(
        string typeName,
        bool asRecord,
        IReadOnlyList<AnonymousMember> members,
        SemanticModel semanticModel,
        int insertPosition)
    {
        var properties = members.Select(member =>
        {
            var typeText = ContextValidTypeHelpers.ToContextValidTypeName(member.Type, semanticModel, insertPosition);
            var accessors = new[]
            {
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(
                        asRecord ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            };

            return SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(typeText), CreateIdentifier(member.Name))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));
        });

        TypeDeclarationSyntax declaration;
        if (asRecord)
        {
            declaration = SyntaxFactory.RecordDeclaration(
                    default,
                    SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                    SyntaxFactory.Token(SyntaxKind.RecordKeyword),
                    CreateIdentifier(typeName),
                    typeParameterList: null,
                    parameterList: null,
                    baseList: null,
                    default,
                    SyntaxFactory.Token(SyntaxKind.OpenBraceToken),
                    SyntaxFactory.List<MemberDeclarationSyntax>(properties),
                    SyntaxFactory.Token(SyntaxKind.CloseBraceToken),
                    default);
        }
        else
        {
            declaration = SyntaxFactory.ClassDeclaration(CreateIdentifier(typeName))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(properties));
        }

        return declaration.NormalizeWhitespace();
    }

    internal static ExpressionSyntax ToNamedCreation(
        AnonymousObjectCreationExpressionSyntax creation,
        string typeName,
        IReadOnlyList<AnonymousMember> members)
    {
        var assignments = new List<ExpressionSyntax>();
        for (var i = 0; i < creation.Initializers.Count; i++)
        {
            var initializer = creation.Initializers[i];
            var name = i < members.Count
                ? members[i].Name
                : InferAnonymousMemberName(initializer);
            var value = initializer.Expression.WithoutTrivia();
            assignments.Add(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(CreateIdentifier(name)).WithTrailingTrivia(SyntaxFactory.Space),
                    value.WithLeadingTrivia(SyntaxFactory.Space)));
        }

        var separators = assignments.Count <= 1
            ? Array.Empty<SyntaxToken>()
            : Enumerable.Repeat(
                SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
                assignments.Count - 1).ToArray();

        var initializerExpr = SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList(assignments, separators))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                .WithLeadingTrivia(SyntaxFactory.Space));

        return SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.Token(SyntaxKind.NewKeyword),
                SyntaxFactory.ParseTypeName(typeName).WithLeadingTrivia(SyntaxFactory.Space),
                argumentList: null,
                initializerExpr)
            .WithLeadingTrivia(creation.GetLeadingTrivia())
            .WithTrailingTrivia(creation.GetTrailingTrivia());
    }

    internal static string InferAnonymousMemberName(AnonymousObjectMemberDeclaratorSyntax initializer)
    {
        if (initializer.NameEquals != null)
            return initializer.NameEquals.Name.Identifier.Text;

        return initializer.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            ConditionalAccessExpressionSyntax { WhenNotNull: MemberBindingExpressionSyntax binding } =>
                binding.Name.Identifier.Text,
            _ => throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "Could not infer an anonymous member name.")
        };
    }

    internal static void ValidateMembersForGeneratedType(
        IReadOnlyList<AnonymousMember> members,
        SemanticModel semanticModel,
        int insertPosition)
    {
        foreach (var member in members)
        {
            if (ContextValidTypeHelpers.IsLessAccessibleThanPublic(member.Type))
            {
                throw new RefactoringException(
                    ErrorCodes.BreaksAccessibility,
                    $"Anonymous member type '{member.Type.ToDisplayString()}' is less accessible than the generated public type.");
            }

            if (!ContextValidTypeHelpers.MemberTypeBindsAtInsertion(member.Type, semanticModel, insertPosition))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Anonymous member type '{member.Type.ToDisplayString()}' is not available at the generated type location.");
            }
        }
    }

    internal static SyntaxToken CreateIdentifier(string name)
    {
        var bare = name.StartsWith('@') ? name[1..] : name;
        var keywordKind = SyntaxFacts.GetKeywordKind(bare);
        if (keywordKind != SyntaxKind.None)
            return SyntaxFactory.VerbatimIdentifier(default, bare, bare, default);

        return SyntaxFactory.Identifier(bare);
    }

    internal readonly record struct AnonymousMember(string Name, ITypeSymbol Type);

    private async Task ValidateNoNameConflictAsync(
        Document document,
        string newTypeName,
        string? targetNamespace,
        CancellationToken cancellationToken)
    {
        var qualified = string.IsNullOrEmpty(targetNamespace)
            ? newTypeName
            : $"{targetNamespace}.{newTypeName}";

        var existing = await TypeResolver.FindTypeByNameAsync(qualified, cancellationToken);
        if (existing != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameConflictScope,
                $"Type '{newTypeName}' already exists in scope.");
        }

        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
            return;

        var metadata = compilation.GetTypeByMetadataName(qualified);
        if (metadata != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameConflictScope,
                $"Type '{newTypeName}' already exists in scope.");
        }

        var bare = SyntaxIdentifierValidation.NormalizeIdentifier(newTypeName);
        var simpleMatches = compilation.GetSymbolsWithName(
            name => name == bare || name == newTypeName,
            SymbolFilter.Type,
            cancellationToken);

        foreach (var symbol in simpleMatches.OfType<INamedTypeSymbol>())
        {
            if (NamespaceEqualityHelpers.NamespacesEqual(symbol.ContainingNamespace, targetNamespace))
            {
                throw new RefactoringException(
                    ErrorCodes.NameConflictScope,
                    $"Type '{newTypeName}' already exists in scope.");
            }
        }
    }

    private async Task<List<CreationTarget>> CollectSameShapeCreationsAsync(
        Project project,
        INamedTypeSymbol anonymousType,
        CancellationToken cancellationToken)
    {
        var targets = new Dictionary<(DocumentId Id, TextSpan Span), CreationTarget>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null || !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                continue;

            foreach (var creation in root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>())
            {
                var type = model.GetTypeInfo(creation, cancellationToken).Type;
                if (!SharesAnonymousType(type, anonymousType))
                    continue;

                targets[(document.Id, creation.Span)] = new CreationTarget(document, creation.Span);
            }
        }

        foreach (var ctor in anonymousType.InstanceConstructors)
        {
            var references = await SymbolFinder.FindReferencesAsync(
                ctor, Context.Solution, cancellationToken);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.Location.Kind != LocationKind.SourceFile)
                        continue;

                    var document = location.Document;
                    if (document.Project.Id != project.Id)
                        continue;

                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null)
                        continue;

                    var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    var creation = node.AncestorsAndSelf()
                        .OfType<AnonymousObjectCreationExpressionSyntax>()
                        .FirstOrDefault();
                    if (creation == null)
                        continue;

                    targets[(document.Id, creation.Span)] = new CreationTarget(document, creation.Span);
                }
            }
        }

        return targets.Values.ToList();
    }

    private static async Task<Solution> ApplyChangesAsync(
        Document originatingDocument,
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<CreationTarget> creations,
        IReadOnlyList<AnonymousMember> members,
        string newTypeName,
        string? targetNamespace,
        CancellationToken cancellationToken)
    {
        var solution = originatingDocument.Project.Solution;
        var documentIds = creations.Select(c => c.Document.Id).ToHashSet();
        documentIds.Add(originatingDocument.Id);

        foreach (var documentId in documentIds)
        {
            var document = solution.GetDocument(documentId)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var documentSpans = creations
                .Where(c => c.Document.Id == documentId)
                .Select(c => c.Span)
                .ToHashSet();

            var nodes = root.DescendantNodes()
                .OfType<AnonymousObjectCreationExpressionSyntax>()
                .Where(n => documentSpans.Contains(n.Span))
                .ToList();

            if (nodes.Count == 0)
                continue;

            root = root.ReplaceNodes(nodes, (original, _) =>
                ToNamedCreation(original, TypeNameForCreation(original, newTypeName, targetNamespace), members));

            if (documentId == originatingDocument.Id)
            {
                var insertionHost = TypeInsertionHelpers.FindNamespace(root, targetNamespace) ?? root;
                var typeToInsert = typeDeclaration
                    .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed, SyntaxFactory.ElasticCarriageReturnLineFeed);
                root = TypeInsertionHelpers.InsertTypeDeclaration(root, insertionHost, typeToInsert);
            }

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    private static string TypeNameForCreation(SyntaxNode creation, string newTypeName, string? targetNamespace)
    {
        var creationNamespace = NamespaceNameHelpers.GetContainingNamespaceName(creation);
        if (NamespaceEqualityHelpers.NamespacesEqual(creationNamespace, targetNamespace))
            return newTypeName;

        var root = creation.SyntaxTree.GetRoot();
        if (!string.IsNullOrEmpty(targetNamespace) && HasUsing(root, targetNamespace))
            return newTypeName;

        return string.IsNullOrEmpty(targetNamespace)
            ? newTypeName
            : $"{targetNamespace}.{newTypeName}";
    }

    private static bool HasUsing(SyntaxNode root, string namespaceName)
    {
        return root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Any(u => u.Name != null && u.Name.ToString() == namespaceName);
    }

    private static bool MembersMatch(IReadOnlyList<AnonymousMember> left, IReadOnlyList<AnonymousMember> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal))
                return false;
            if (!SymbolEqualityComparer.Default.Equals(left[i].Type, right[i].Type))
                return false;
        }

        return true;
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        ConvertAnonymousToClassParams @params,
        Document originalDocument,
        Solution newSolution,
        CancellationToken cancellationToken)
    {
        var pendingChanges = new List<PendingChange>();
        var originalSolution = originalDocument.Project.Solution;
        var kind = @params.AsRecord ? "record" : "class";

        foreach (var projectChanges in newSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = originalSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                if (oldDoc?.FilePath == null || newDoc == null)
                    continue;

                var before = await oldDoc.GetTextAsync(cancellationToken);
                var after = await newDoc.GetTextAsync(cancellationToken);
                pendingChanges.Add(new PendingChange
                {
                    File = oldDoc.FilePath,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Convert anonymous type to {kind} '{@params.NewTypeName}'",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }

            foreach (var docId in projectChanges.GetAddedDocuments())
            {
                var newDoc = newSolution.GetDocument(docId);
                if (newDoc?.FilePath == null)
                    continue;

                var after = await newDoc.GetTextAsync(cancellationToken);
                pendingChanges.Add(new PendingChange
                {
                    File = newDoc.FilePath,
                    ChangeType = ChangeKind.Create,
                    Description = $"Create {kind} '{@params.NewTypeName}'",
                    BeforeSnippet = "// (new file)",
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = $"Convert anonymous type to {kind} '{@params.NewTypeName}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record CreationTarget(Document Document, TextSpan Span);
}

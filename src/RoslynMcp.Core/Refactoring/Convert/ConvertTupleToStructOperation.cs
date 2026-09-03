using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;
using SymbolKind = RoslynMcp.Contracts.Enums.SymbolKind;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts a C# tuple / <c>ValueTuple</c> creation to a named struct
/// and replaces same-shape tuple creations that share that tuple type.
/// </summary>
public sealed class ConvertTupleToStructOperation : RefactoringOperationBase<ConvertTupleToStructParams>
{
    /// <summary>
    /// Creates a new convert-tuple-to-struct operation.
    /// </summary>
    public ConvertTupleToStructOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertTupleToStructParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-tuple-to-struct inputs. Internal so tests can exercise
    /// rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertTupleToStructParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.NewTypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newTypeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidTypeName(@params.NewTypeName))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolName,
                $"'{@params.NewTypeName}' is not a valid C# type name.");
        }
    }

    /// <summary>
    /// Rejects documents that cannot receive source edits.
    /// </summary>
    internal static void ValidateDocumentIsEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (source-generated).");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable.");
        }

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertTupleToStructParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var creation = FindTupleCreation(root, semanticModel, @params);
        var tupleType = GetTupleType(semanticModel, creation);
        var members = GetTupleMembers(tupleType);
        var targetNamespace = GetContainingNamespaceName(semanticModel, creation);
        var lookupName = StripVerbatimPrefix(@params.NewTypeName);

        if (!ContextAcceptsStructReplacement(creation, semanticModel))
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "The selected tuple is used in a tuple-typed context that cannot accept the generated struct.");
        }

        await ValidateNoNameConflictAsync(document, lookupName, targetNamespace, cancellationToken);

        var creations = await CollectSameShapeCreationsAsync(document, tupleType, cancellationToken);

        if (creations.Count == 0)
            creations.Add(new CreationTarget(document, creation.Span));

        foreach (var target in creations)
            ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var insertPosition = GetTypeInsertionPosition(root, creation);
        ValidateMembersForGeneratedType(members, semanticModel, insertPosition);
        var typeDeclaration = CreateNamedStruct(
            lookupName,
            members,
            semanticModel,
            insertPosition);

        var newSolution = await ApplyChangesAsync(
            document,
            typeDeclaration,
            creations,
            members,
            lookupName,
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
        var emittedName = EmitTypeName(lookupName);
        var qualifiedName = string.IsNullOrEmpty(targetNamespace)
            ? emittedName
            : $"{targetNamespace}.{emittedName}";

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
                Name = lookupName,
                FullyQualifiedName = qualifiedName,
                Kind = SymbolKind.Struct
            },
            creations.Count,
            0);
    }

    internal static bool IsValidTypeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.StartsWith('@') && name.Length > 1)
        {
            var bare = name[1..];
            return SyntaxFacts.IsValidIdentifier(bare) ||
                   SyntaxFacts.GetKeywordKind(bare) != SyntaxKind.None;
        }

        if (!SyntaxFacts.IsValidIdentifier(name))
            return false;

        var keywordKind = SyntaxFacts.GetKeywordKind(name);
        return keywordKind == SyntaxKind.None || !SyntaxFacts.IsReservedKeyword(keywordKind);
    }

    internal static ExpressionSyntax FindTupleCreation(
        SyntaxNode root,
        SemanticModel semanticModel,
        ConvertTupleToStructParams @params)
    {
        var candidates = root.DescendantNodes()
            .Where(n => IsTupleCreationNode(n, semanticModel))
            .Where(n => SpanCoversLine(n.GetLocation().GetLineSpan(), @params.Line, @params.Column))
            .ToList();

        if (candidates.Count == 1)
            return (ExpressionSyntax)candidates[0];

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"No tuple found at line {@params.Line}.");
        }

        if (@params.Column.HasValue)
        {
            var atColumn = candidates
                .Where(n => SpanCoversColumn(n.GetLocation().GetLineSpan(), @params.Line, @params.Column.Value))
                .ToList();
            if (atColumn.Count == 1)
                return (ExpressionSyntax)atColumn[0];
        }

        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple tuple expressions found at line {@params.Line}. Provide column.");
    }

    internal static INamedTypeSymbol GetTupleType(
        SemanticModel semanticModel,
        ExpressionSyntax creation)
    {
        var type = GetTupleLikeType(semanticModel, creation);
        if (type == null)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "The selected expression is not a tuple.");
        }

        return type;
    }

    internal static IReadOnlyList<TupleMember> GetTupleMembers(INamedTypeSymbol tupleType)
    {
        if (tupleType.IsTupleType && !tupleType.TupleElements.IsDefault)
        {
            return tupleType.TupleElements
                .Select(e => new TupleMember(e.Name, e.Type))
                .ToList();
        }

        return tupleType.TypeArguments
            .Select((type, index) => new TupleMember($"Item{index + 1}", type))
            .ToList();
    }

    internal static bool IsTupleLike(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error)
            return false;
        if (named.IsTupleType)
            return true;

        var definition = named.OriginalDefinition;
        return definition.Name == "ValueTuple" &&
               definition.ContainingNamespace?.ToDisplayString() == "System";
    }

    internal static INamedTypeSymbol? GetTupleLikeType(SemanticModel semanticModel, SyntaxNode node)
    {
        var info = semanticModel.GetTypeInfo(node);
        if (IsTupleLike(info.Type))
            return (INamedTypeSymbol)info.Type!;
        if (IsTupleLike(info.ConvertedType))
            return (INamedTypeSymbol)info.ConvertedType!;

        if (semanticModel.GetSymbolInfo(node).Symbol is IMethodSymbol method &&
            IsTupleLike(method.ContainingType))
        {
            return method.ContainingType;
        }

        return null;
    }

    internal static bool SharesTupleType(ITypeSymbol? candidate, INamedTypeSymbol original)
    {
        if (!IsTupleLike(candidate) || candidate is not INamedTypeSymbol named)
            return false;

        if (SymbolEqualityComparer.Default.Equals(named, original))
            return true;

        return MembersMatch(GetTupleMembers(named), GetTupleMembers(original));
    }

    internal static TypeDeclarationSyntax CreateNamedStruct(
        string typeName,
        IReadOnlyList<TupleMember> members,
        SemanticModel semanticModel,
        int insertPosition)
    {
        var properties = members.Select(member =>
        {
            var typeText = ToContextValidTypeName(member.Type, semanticModel, insertPosition);
            var accessors = new[]
            {
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            };

            return SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(typeText), CreateIdentifier(member.Name))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));
        });

        return SyntaxFactory.StructDeclaration(typeName)
            .WithIdentifier(CreateIdentifier(typeName))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(properties))
            .NormalizeWhitespace();
    }

    internal static ExpressionSyntax ToNamedCreation(
        ExpressionSyntax creation,
        string typeName,
        IReadOnlyList<TupleMember> members,
        SemanticModel? semanticModel = null)
    {
        var values = GetCreationValues(creation, members, semanticModel);
        var assignments = new List<ExpressionSyntax>();
        for (var i = 0; i < values.Count; i++)
        {
            var name = i < members.Count ? members[i].Name : $"Item{i + 1}";
            assignments.Add(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(CreateIdentifier(name)).WithTrailingTrivia(SyntaxFactory.Space),
                    values[i].WithLeadingTrivia(SyntaxFactory.Space)));
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

    internal static string ToContextValidTypeName(ITypeSymbol type, SemanticModel model, int position)
    {
        var display = type.ToMinimalDisplayString(model, position);
        if (string.IsNullOrWhiteSpace(display) || TypeNameBindsToDifferentType(display, type, model, position))
        {
            display = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        }

        return display;
    }

    internal static void ValidateMembersForGeneratedType(
        IReadOnlyList<TupleMember> members,
        SemanticModel semanticModel,
        int insertPosition)
    {
        foreach (var member in members)
        {
            if (IsLessAccessibleThanPublic(member.Type))
            {
                throw new RefactoringException(
                    ErrorCodes.BreaksAccessibility,
                    $"Tuple member type '{member.Type.ToDisplayString()}' is less accessible than the generated public type.");
            }

            if (!MemberTypeBindsAtInsertion(member.Type, semanticModel, insertPosition))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Tuple member type '{member.Type.ToDisplayString()}' is not available at the generated type location.");
            }
        }
    }

    internal static bool MemberTypeBindsAtInsertion(ITypeSymbol type, SemanticModel model, int position)
    {
        if (ContainsTypeParameter(type))
            return false;

        var display = ToContextValidTypeName(type, model, position);
        return !TypeNameBindsToDifferentType(display, type, model, position);
    }

    internal static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
            return true;

        if (type is IArrayTypeSymbol array)
            return ContainsTypeParameter(array.ElementType);

        if (type is IPointerTypeSymbol pointer)
            return ContainsTypeParameter(pointer.PointedAtType);

        if (type is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (ContainsTypeParameter(argument))
                    return true;
            }
        }

        return false;
    }

    internal static bool IsLessAccessibleThanPublic(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return IsLessAccessibleThanPublic(array.ElementType);

        if (type is IPointerTypeSymbol pointer)
            return IsLessAccessibleThanPublic(pointer.PointedAtType);

        if (type is INamedTypeSymbol named)
        {
            if (GetEffectiveAccessibility(named) is not (Accessibility.Public or Accessibility.NotApplicable))
                return true;

            foreach (var argument in named.TypeArguments)
            {
                if (IsLessAccessibleThanPublic(argument))
                    return true;
            }
        }

        return false;
    }

    internal static Accessibility GetEffectiveAccessibility(ISymbol symbol)
    {
        var current = symbol.DeclaredAccessibility;
        for (var container = symbol.ContainingType; container != null; container = container.ContainingType)
            current = MinAccessibility(current, container.DeclaredAccessibility);

        return current;
    }

    internal static SyntaxToken CreateIdentifier(string name)
    {
        var bare = StripVerbatimPrefix(name);
        var keywordKind = SyntaxFacts.GetKeywordKind(bare);
        if (keywordKind != SyntaxKind.None)
            return SyntaxFactory.VerbatimIdentifier(default, bare, bare, default);

        return SyntaxFactory.Identifier(bare);
    }

    /// <summary>
    /// Strips a leading <c>@</c> so <c>@Point</c> and <c>Point</c> share the same lookup name.
    /// </summary>
    internal static string StripVerbatimPrefix(string name) =>
        name.StartsWith('@') && name.Length > 1 ? name[1..] : name;

    /// <summary>
    /// Emits a type name, adding <c>@</c> only when the identifier is a keyword.
    /// </summary>
    internal static string EmitTypeName(string name)
    {
        var bare = StripVerbatimPrefix(name);
        var keywordKind = SyntaxFacts.GetKeywordKind(bare);
        return keywordKind != SyntaxKind.None ? "@" + bare : bare;
    }

    internal static string? GetContainingNamespaceName(SemanticModel semanticModel, SyntaxNode node)
    {
        var enclosing = semanticModel.GetEnclosingSymbol(node.SpanStart);
        var fromSymbol = ToNamespaceName(enclosing?.ContainingNamespace);
        if (!string.IsNullOrEmpty(fromSymbol))
            return fromSymbol;

        return GetContainingNamespaceName(node);
    }

    internal static string? GetContainingNamespaceName(SyntaxNode node)
    {
        var name = GetFullNamespaceName(
            node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault());
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Joins nested namespace declarations into the full enclosing name
    /// (e.g. <c>Outer.Inner</c>, not <c>Inner</c>).
    /// </summary>
    internal static string? GetFullNamespaceName(BaseNamespaceDeclarationSyntax? ns)
    {
        if (ns == null)
            return null;

        var parts = ns.AncestorsAndSelf()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(n => n.Name.ToString())
            .Where(part => !string.IsNullOrEmpty(part));

        var joined = string.Join(".", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    internal readonly record struct TupleMember(string Name, ITypeSymbol Type);

    private async Task ValidateNoNameConflictAsync(
        Document document,
        string newTypeName,
        string? targetNamespace,
        CancellationToken cancellationToken)
    {
        var lookupName = StripVerbatimPrefix(newTypeName);
        var qualified = string.IsNullOrEmpty(targetNamespace)
            ? lookupName
            : $"{targetNamespace}.{lookupName}";

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

        var simpleMatches = compilation.GetSymbolsWithName(
            name => name == lookupName,
            SymbolFilter.Type,
            cancellationToken);

        foreach (var symbol in simpleMatches.OfType<INamedTypeSymbol>())
        {
            if (NamespacesEqual(symbol.ContainingNamespace, targetNamespace))
            {
                throw new RefactoringException(
                    ErrorCodes.NameConflictScope,
                    $"Type '{newTypeName}' already exists in scope.");
            }
        }
    }

    private async Task<List<CreationTarget>> CollectSameShapeCreationsAsync(
        Document originatingDocument,
        INamedTypeSymbol tupleType,
        CancellationToken cancellationToken)
    {
        var targets = new Dictionary<(DocumentId Id, TextSpan Span), CreationTarget>();
        var originatingProject = originatingDocument.Project;

        foreach (var project in Context.Solution.Projects)
        {
            if (!ProjectCanReference(project, originatingProject))
                continue;

            foreach (var document in project.Documents)
            {
                if (document.FilePath == null || !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                    continue;

                foreach (var node in root.DescendantNodes())
                {
                    if (!IsTupleCreationNode(node, model))
                        continue;

                    var type = GetTupleLikeType(model, node);
                    if (!SharesTupleType(type, tupleType))
                        continue;

                    if (node is not ExpressionSyntax expression ||
                        !ContextAcceptsStructReplacement(expression, model))
                    {
                        continue;
                    }

                    targets[(document.Id, node.Span)] = new CreationTarget(document, node.Span);
                }
            }
        }

        return targets.Values.ToList();
    }

    private static async Task<Solution> ApplyChangesAsync(
        Document originatingDocument,
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<CreationTarget> creations,
        IReadOnlyList<TupleMember> members,
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
                .OfType<ExpressionSyntax>()
                .Where(n => documentSpans.Contains(n.Span) && IsRewritableCreationSyntax(n))
                .ToList();

            if (nodes.Count == 0 && documentId != originatingDocument.Id)
                continue;

            if (nodes.Count > 0)
            {
                var model = await document.GetSemanticModelAsync(cancellationToken);
                var emittedName = EmitTypeName(newTypeName);
                root = root.ReplaceNodes(nodes, (original, _) =>
                    ToNamedCreation(
                        original,
                        TypeNameForCreation(original, emittedName, targetNamespace),
                        members,
                        model));
            }

            if (documentId == originatingDocument.Id)
            {
                var insertionHost = FindNamespace(root, targetNamespace) ?? root;
                var typeToInsert = typeDeclaration
                    .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed, SyntaxFactory.ElasticCarriageReturnLineFeed);
                root = InsertTypeDeclaration(root, insertionHost, typeToInsert);
            }

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    private static SyntaxNode InsertTypeDeclaration(
        SyntaxNode root,
        SyntaxNode? insertionHost,
        TypeDeclarationSyntax typeDeclaration)
    {
        switch (insertionHost)
        {
            case BaseNamespaceDeclarationSyntax ns:
                return root.ReplaceNode(ns, ns.AddMembers(typeDeclaration));
            case CompilationUnitSyntax:
                return ((CompilationUnitSyntax)root).AddMembers(typeDeclaration);
            default:
                if (root is CompilationUnitSyntax compilationUnit)
                    return compilationUnit.AddMembers(typeDeclaration);
                throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    "Could not find a compilable location for the new type.");
        }
    }

    private static BaseNamespaceDeclarationSyntax? FindNamespace(SyntaxNode root, string? targetNamespace)
    {
        if (string.IsNullOrEmpty(targetNamespace))
            return null;

        return root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .LastOrDefault(ns => string.Equals(GetFullNamespaceName(ns), targetNamespace, StringComparison.Ordinal));
    }

    private static int GetTypeInsertionPosition(SyntaxNode root, SyntaxNode creation)
    {
        var ns = creation.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is NamespaceDeclarationSyntax blockNamespace)
            return blockNamespace.CloseBraceToken.SpanStart;
        if (ns != null)
            return ns.Span.End;
        return root.Span.End;
    }

    private static string TypeNameForCreation(SyntaxNode creation, string newTypeName, string? targetNamespace)
    {
        var creationNamespace = GetContainingNamespaceName(creation);
        if (NamespacesEqual(creationNamespace, targetNamespace))
            return newTypeName;

        if (!string.IsNullOrEmpty(targetNamespace) && HasUsingInScope(creation, targetNamespace))
            return newTypeName;

        return string.IsNullOrEmpty(targetNamespace)
            ? newTypeName
            : $"{targetNamespace}.{newTypeName}";
    }

    private static bool NamespacesEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right))
            return true;
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when an ordinary (non-alias, non-static) namespace import of
    /// <paramref name="namespaceName"/> is in scope at <paramref name="creation"/>.
    /// Usings inside unrelated namespace blocks do not count.
    /// </summary>
    internal static bool HasUsingInScope(SyntaxNode creation, string namespaceName)
    {
        foreach (var container in creation.AncestorsAndSelf())
        {
            SyntaxList<UsingDirectiveSyntax> usings = default;
            switch (container)
            {
                case CompilationUnitSyntax compilationUnit:
                    usings = compilationUnit.Usings;
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    usings = ns.Usings;
                    break;
                default:
                    continue;
            }

            if (usings.Any(u => IsOrdinaryNamespaceImport(u, namespaceName)))
                return true;
        }

        return false;
    }

    internal static bool IsOrdinaryNamespaceImport(UsingDirectiveSyntax directive, string namespaceName)
    {
        if (directive.Alias != null)
            return false;
        if (directive.StaticKeyword != default)
            return false;
        return directive.Name != null &&
               string.Equals(directive.Name.ToString(), namespaceName, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="from"/> is the originating project or already
    /// references it (so the generated struct is visible without adding a cycle).
    /// </summary>
    internal static bool ProjectCanReference(Project from, Project target)
    {
        if (from.Id == target.Id)
            return true;

        var seen = new HashSet<ProjectId>();
        var queue = new Queue<ProjectId>();
        queue.Enqueue(from.Id);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id))
                continue;

            var project = from.Solution.GetProject(id);
            if (project == null)
                continue;

            foreach (var reference in project.ProjectReferences)
            {
                if (reference.ProjectId == target.Id)
                    return true;
                queue.Enqueue(reference.ProjectId);
            }
        }

        return false;
    }

    /// <summary>
    /// A generated public struct is accepted when the context re-infers (<c>var</c>/discard)
    /// or already converts the tuple to a destination a plain struct can satisfy
    /// (<c>object</c>, <c>dynamic</c>, <c>ValueType</c>). Explicit tuple-typed
    /// returns, parameters, and locals are rejected.
    /// </summary>
    internal static bool ContextAcceptsStructReplacement(ExpressionSyntax creation, SemanticModel model)
    {
        if (IsImplicitlyTypedOrDiscardContext(creation))
            return true;

        var converted = model.GetTypeInfo(creation).ConvertedType;
        if (converted == null || converted.TypeKind == TypeKind.Error)
            return false;

        if (converted.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType)
            return true;

        if (converted.TypeKind == TypeKind.Dynamic)
            return true;

        if (converted is INamedTypeSymbol named && named.IsTupleType)
            return false;

        return false;
    }

    internal static bool IsImplicitlyTypedOrDiscardContext(ExpressionSyntax creation)
    {
        SyntaxNode node = creation;
        while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
            node = parenthesized;

        if (node.Parent is EqualsValueClauseSyntax equals &&
            equals.Parent is VariableDeclaratorSyntax declarator &&
            declarator.Parent is VariableDeclarationSyntax declaration)
        {
            return declaration.Type.IsVar;
        }

        if (node.Parent is AssignmentExpressionSyntax assignment &&
            assignment.Right == node &&
            assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            return true;
        }

        return false;
    }

    private static string? ToNamespaceName(INamespaceSymbol? symbol)
    {
        if (symbol == null || symbol.IsGlobalNamespace)
            return null;

        var name = symbol.ToDisplayString();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static bool NamespacesEqual(INamespaceSymbol? symbol, string? name)
    {
        if (symbol == null || symbol.IsGlobalNamespace)
            return string.IsNullOrEmpty(name);
        return string.Equals(symbol.ToDisplayString(), name, StringComparison.Ordinal);
    }

    private static bool MembersMatch(IReadOnlyList<TupleMember> left, IReadOnlyList<TupleMember> right)
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

    private static bool IsTupleCreationNode(SyntaxNode node, SemanticModel semanticModel)
    {
        switch (node)
        {
            case TupleExpressionSyntax tuple:
                return IsTupleCreation(tuple);
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
                return GetTupleLikeType(semanticModel, node) != null;
            default:
                return false;
        }
    }

    private static bool IsRewritableCreationSyntax(SyntaxNode node) =>
        node is TupleExpressionSyntax
            or ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax;

    /// <summary>
    /// Tuple expressions on the left of an assignment are deconstruction targets, not creations.
    /// </summary>
    internal static bool IsTupleCreation(TupleExpressionSyntax tuple) =>
        tuple.Parent is not AssignmentExpressionSyntax assignment || assignment.Left != tuple;

    internal static IReadOnlyList<ExpressionSyntax> GetCreationValues(
        ExpressionSyntax creation,
        IReadOnlyList<TupleMember> members,
        SemanticModel? semanticModel = null)
    {
        return creation switch
        {
            TupleExpressionSyntax tuple => tuple.Arguments
                .Select(a => a.Expression.WithoutTrivia())
                .ToList(),
            ObjectCreationExpressionSyntax objectCreation =>
                MapConstructorArguments(objectCreation.ArgumentList, objectCreation, members, semanticModel),
            ImplicitObjectCreationExpressionSyntax implicitCreation =>
                MapConstructorArguments(implicitCreation.ArgumentList, implicitCreation, members, semanticModel),
            _ => throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "The selected expression is not a tuple creation.")
        };
    }

    internal static IReadOnlyList<ExpressionSyntax> MapConstructorArguments(
        ArgumentListSyntax? argumentList,
        ExpressionSyntax creation,
        IReadOnlyList<TupleMember> members,
        SemanticModel? semanticModel)
    {
        if (argumentList == null || argumentList.Arguments.Count == 0)
            return [];

        var ctor = semanticModel?.GetSymbolInfo(creation).Symbol as IMethodSymbol;
        var mapped = new ExpressionSyntax?[members.Count];
        var used = new bool[members.Count];
        var nextPositional = 0;

        foreach (var argument in argumentList.Arguments)
        {
            int index;
            if (argument.NameColon != null)
            {
                index = IndexOfConstructorParameter(ctor, members, argument.NameColon.Name.Identifier.ValueText);
            }
            else
            {
                while (nextPositional < members.Count && used[nextPositional])
                    nextPositional++;
                index = nextPositional++;
            }

            if (index < 0 || index >= members.Count)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    "Could not map a constructor argument to a tuple element.");
            }

            mapped[index] = argument.Expression.WithoutTrivia();
            used[index] = true;
        }

        var values = new List<ExpressionSyntax>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            if (mapped[i] == null)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    "Could not map a constructor argument to a tuple element.");
            }

            values.Add(mapped[i]!);
        }

        return values;
    }

    internal static int IndexOfConstructorParameter(
        IMethodSymbol? constructor,
        IReadOnlyList<TupleMember> members,
        string argumentName)
    {
        if (constructor != null)
        {
            for (var i = 0; i < constructor.Parameters.Length && i < members.Count; i++)
            {
                if (string.Equals(constructor.Parameters[i].Name, argumentName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        for (var i = 0; i < members.Count; i++)
        {
            if (string.Equals(members[i].Name, argumentName, StringComparison.OrdinalIgnoreCase))
                return i;
            if (string.Equals($"Item{i + 1}", argumentName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    internal static bool SpanCoversLine(FileLinePositionSpan span, int line, int? column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        if (line < startLine || line > endLine)
            return false;

        if (!column.HasValue)
            return true;

        return SpanCoversColumn(span, line, column.Value);
    }

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character after a covered tuple
    /// creation also match the previous creation. Same helper as
    /// <c>ConvertAnonymousToClassOperation.SpanCoversColumn</c> /
    /// <c>ConvertForeachLinqOperation.SpanCoversColumn</c> /
    /// <c>RemoveBracesOperation.SpanCoversColumn</c> /
    /// <c>AddBracesOperation.SpanCoversColumn</c> /
    /// <c>SimplifyNameOperation.SpanCoversColumn</c> /
    /// <c>InvertIfOperation.SpanCoversColumn</c>.
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

    private static bool TypeNameBindsToDifferentType(
        string display,
        ITypeSymbol expected,
        SemanticModel model,
        int position)
    {
        var parsed = SyntaxFactory.ParseTypeName(display);
        var spec = model.GetSpeculativeTypeInfo(position, parsed, SpeculativeBindingOption.BindAsTypeOrNamespace);
        if (spec.Type == null || spec.Type.TypeKind == TypeKind.Error)
            return true;

        return !SymbolEqualityComparer.Default.Equals(spec.Type, expected);
    }

    private static Accessibility MinAccessibility(Accessibility left, Accessibility right)
    {
        return AccessibilityRank(left) <= AccessibilityRank(right) ? left : right;
    }

    private static int AccessibilityRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => 0,
        Accessibility.ProtectedAndInternal => 1,
        Accessibility.Protected => 2,
        Accessibility.Internal => 3,
        Accessibility.ProtectedOrInternal => 4,
        Accessibility.Public => 5,
        _ => 5
    };

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        ConvertTupleToStructParams @params,
        Document originalDocument,
        Solution newSolution,
        CancellationToken cancellationToken)
    {
        var pendingChanges = new List<PendingChange>();
        var originalSolution = originalDocument.Project.Solution;

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
                    Description = $"Convert tuple to struct '{@params.NewTypeName}'",
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
                    Description = $"Create struct '{@params.NewTypeName}'",
                    BeforeSnippet = "// (new file)",
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Convert tuple to struct '{@params.NewTypeName}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record CreationTarget(Document Document, TextSpan Span);
}

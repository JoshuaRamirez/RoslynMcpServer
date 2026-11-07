using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using SymbolKind = RoslynMcp.Contracts.Enums.SymbolKind;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts a C# tuple / <c>ValueTuple</c> creation to a named struct
/// or <c>record struct</c> and replaces same-shape tuple creations that
/// share that tuple type.
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

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!SyntaxIdentifierValidation.IsValidIdentifier(@params.NewTypeName))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolName,
                $"'{@params.NewTypeName}' is not a valid C# type name.");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertTupleToStructParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var creation = FindTupleCreation(root, semanticModel, @params);
        var tupleType = GetTupleType(semanticModel, creation);
        var members = GetTupleMembers(tupleType);
        var targetNamespace = NamespaceNameHelpers.GetContainingNamespaceName(semanticModel, creation);
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
            DocumentEditableHelpers.ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var insertPosition = TypeInsertionHelpers.GetTypeInsertionPosition(root, creation);
        if (@params.AsRecord)
            await ValidateRecordStructLanguageVersionAsync(creations, cancellationToken);

        // By-ref / init-breaking gates run before member-type availability so a
        // constrained value-type receiver (T : struct, ICounter) is refused for the
        // mutating call rather than only for "type parameter not available" (#3347).
        if (@params.AsRecord)
            await ValidateNoInitBreakingElementWritesAsync(creations, members, tupleType, cancellationToken);
        // Both generated kinds expose properties, so neither supports __makeref
        // or other field-only uses. Keep the record write gate first for init diagnostics.
        await ValidateNoByRefElementUsesAsync(creations, members, tupleType, cancellationToken);
        ValidateMembersForGeneratedType(members, semanticModel, insertPosition, @params.AsRecord);
        var typeDeclaration = CreateNamedStruct(
            lookupName,
            @params.AsRecord,
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
                Kind = @params.AsRecord ? SymbolKind.Record : SymbolKind.Struct
            },
            creations.Count,
            0);
    }

    internal static ExpressionSyntax FindTupleCreation(
        SyntaxNode root,
        SemanticModel semanticModel,
        ConvertTupleToStructParams @params)
    {
        var candidates = root.DescendantNodes()
            .Where(n => IsTupleCreationNode(n, semanticModel))
            .Where(n => SpanCoverage.SpanCoversLine(n.GetLocation().GetLineSpan(), @params.Line, @params.Column))
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
                .Where(n => SpanCoverage.SpanCoversColumn(n.GetLocation().GetLineSpan(), @params.Line, @params.Column.Value))
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
        bool asRecord,
        IReadOnlyList<TupleMember> members,
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
                    default)
                .WithClassOrStructKeyword(SyntaxFactory.Token(SyntaxKind.StructKeyword));
        }
        else
        {
            declaration = SyntaxFactory.StructDeclaration(typeName)
                .WithIdentifier(CreateIdentifier(typeName))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(properties));
        }

        return declaration.NormalizeWhitespace();
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

    /// <summary>
    /// Record structs emit <c>init</c> accessors; reject when a converted creation initializes a
    /// local/parameter/field that later receives a tuple-element assignment (CS8852).
    /// </summary>
    private static async Task ValidateNoInitBreakingElementWritesAsync(
        IReadOnlyList<CreationTarget> creations,
        IReadOnlyList<TupleMember> members,
        ITypeSymbol tupleType,
        CancellationToken cancellationToken)
    {
        var memberNames = BuildElementMemberNames(members);

        foreach (var target in creations)
        {
            var root = await target.Document.GetSyntaxRootAsync(cancellationToken);
            var model = await target.Document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                continue;

            var node = root.FindNode(target.Span, getInnermostNodeForTie: true);
            ExpressionSyntax? creation = null;
            for (SyntaxNode? cursor = node; cursor != null; cursor = cursor.Parent)
            {
                if (cursor is ExpressionSyntax expression && IsRewritableCreationSyntax(expression))
                {
                    creation = expression;
                    break;
                }
            }

            if (creation == null)
                continue;

            // One shared escape / propagation routine for the selected creation
            // itself, its assigned storage, and every opaque-return call-site
            // result (Codex P1s on #3441, PRRT_kwDORD0nYs605__Q / …605__Y / …605__c).
            var subject = await FindUnsafeOpaqueReturnFlowAsync(
                target.Document,
                root,
                model,
                creation,
                memberNames,
                includeWrites: true,
                (doc, docRoot, docModel, seedValue, flowStorage) => SeedValueHasUnsafeEscapeAsync(
                    doc, docRoot, docModel, seedValue, flowStorage, memberNames, tupleType,
                    includeWrites: true, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (subject != null)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Cannot convert to a record struct because {subject} has element assignments that require settable properties.");
            }
        }
    }

    /// <summary>
    /// Named structs emit <c>{ get; set; }</c> properties; reject when a converted
    /// creation's storage (or alias) later passes a tuple element by <c>ref</c>/<c>out</c>/<c>in</c>,
    /// takes its address, binds it to a ref local/return (CS0206), or assigns / increments
    /// a nested location through a value-type element (CS1612 — the property returns a
    /// temporary). Direct element assignments and increments remain allowed for non-record
    /// structs (setters work).
    /// </summary>
    private static async Task ValidateNoByRefElementUsesAsync(
        IReadOnlyList<CreationTarget> creations,
        IReadOnlyList<TupleMember> members,
        ITypeSymbol tupleType,
        CancellationToken cancellationToken)
    {
        var memberNames = BuildElementMemberNames(members);

        foreach (var target in creations)
        {
            var root = await target.Document.GetSyntaxRootAsync(cancellationToken);
            var model = await target.Document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                continue;

            var node = root.FindNode(target.Span, getInnermostNodeForTie: true);
            ExpressionSyntax? creation = null;
            for (SyntaxNode? cursor = node; cursor != null; cursor = cursor.Parent)
            {
                if (cursor is ExpressionSyntax expression && IsRewritableCreationSyntax(expression))
                {
                    creation = expression;
                    break;
                }
            }

            if (creation == null)
                continue;

            // Shared escape / propagation routine (see the init-write gate above).
            var subject = await FindUnsafeOpaqueReturnFlowAsync(
                target.Document,
                root,
                model,
                creation,
                memberNames,
                includeWrites: false,
                (doc, docRoot, docModel, seedValue, flowStorage) => SeedValueHasUnsafeEscapeAsync(
                    doc, docRoot, docModel, seedValue, flowStorage, memberNames, tupleType,
                    includeWrites: false, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (subject != null)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Cannot convert because {subject} has an element passed by reference; the generated struct exposes properties, not fields.");
            }
        }
    }

    /// <summary>
    /// A convertible creation with no assigned storage may still escape through a
    /// <c>return</c> / expression body of a method, local function, property, or
    /// lambda whose return type is opaque (<c>object</c>, <c>dynamic</c>, …):
    /// <c>static dynamic Get() =&gt; (1, 2); dynamic copy = Get(); Bump(ref copy.Item1);</c>
    /// binds by-ref to a boxed <c>ValueTuple</c> field that becomes a property after
    /// conversion. Follows every call site of that function (transitively through
    /// further opaque returns) and validates the storage each result is assigned to;
    /// conservatively refuses method-group / escaping-delegate uses, overridable or
    /// interface-implementing functions, and lambdas whose delegate is not a
    /// source-declared local / field / property (Codex P1 on #3441,
    /// PRRT_kwDORD0nYs605DwK). Every visited value — the selected creation itself
    /// and each call-site result — is handed to <paramref name="seedCheck"/> as an
    /// alias-like seed together with its assigned storage (if any), so direct uses
    /// and alias uses share one escape classifier (Codex P1s on #3441,
    /// PRRT_kwDORD0nYs605__Q / …605__Y / …605__c). Returns a description of the
    /// offending value for the diagnostic, or null.
    /// </summary>
    private static async Task<string?> FindUnsafeOpaqueReturnFlowAsync(
        Document document,
        SyntaxNode root,
        SemanticModel model,
        ExpressionSyntax creation,
        HashSet<string> memberNames,
        bool includeWrites,
        Func<Document, SyntaxNode, SemanticModel, ExpressionSyntax, ISymbol?, Task<bool>> seedCheck,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<(Document Document, SyntaxNode Root, SemanticModel Model, ExpressionSyntax Value, string? Function, bool Sequence)>();
        pending.Push((document, root, model, creation, null, false));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentDocument, currentRoot, currentModel, value, function, sequence) = pending.Pop();

            // Climb value-preserving wrappers / containers until storage or a return.
            ExpressionSyntax top = value;
            ISymbol? storage = null;
            while (true)
            {
                storage = TryGetAssignedStorageSymbol(top, currentModel, cancellationToken);
                if (storage != null)
                    break;

                // foreach (dynamic copy in Get()) over an iterator / container
                // result: the iteration variable holds the boxed tuple (Codex P1
                // on #3441, PRRT_kwDORD0nYs605hQG).
                if (top.Parent is ForEachStatementSyntax forEach && forEach.Expression == top)
                {
                    storage = currentModel.GetDeclaredSymbol(forEach, cancellationToken);
                    if (storage != null)
                        break;
                }

                if (!TryClimbOpaqueReturnFlow(top, out var next))
                    break;
                top = next;
            }

            // The value itself is an alias-like seed: every use site (setter /
            // init / indexer value, callee argument, extension receiver, operator /
            // conversion operand, collection insert, …) is classified by the same
            // routine as storage aliases, whether or not storage was found.
            if (await seedCheck(currentDocument, currentRoot, currentModel, value, storage).ConfigureAwait(false))
            {
                return function != null
                    ? $"the tuple returned from '{function}'"
                    : storage != null
                        ? $"'{storage.Name}'"
                        : "the converted tuple creation";
            }

            if (storage != null)
                continue;

            if (function != null && DirectElementUseIsUnsafe(top, memberNames, includeWrites))
                return $"the tuple returned from '{function}'";

            if (!TryGetOpaqueReturningFunction(
                    top, currentModel, cancellationToken, out var functionSymbol, out var lambda, out var isIterator))
            {
                // An iterator's sequence result that reaches neither storage, a
                // foreach, nor a further opaque return (Get().First(), deconstructing
                // foreach, LINQ chains, …) cannot be followed element-wise; refuse
                // unless the result is simply discarded (Codex P1 on #3441,
                // PRRT_kwDORD0nYs605hQG).
                if (sequence && function != null && top.Parent is not ExpressionStatementSyntax)
                    return $"the tuple returned from '{function}'";

                continue;
            }

            if (!visited.Add(functionSymbol))
                continue;

            var functionName = lambda != null ? "lambda" : functionSymbol.Name;

            ISymbol referenceTarget = functionSymbol;
            if (lambda != null)
            {
                SyntaxNode lambdaTop = lambda;
                while (lambdaTop.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
                    lambdaTop = lambdaTop.Parent;

                var delegateStorage = lambdaTop is ExpressionSyntax lambdaExpression
                    ? TryGetAssignedStorageSymbol(lambdaExpression, currentModel, cancellationToken)
                    : null;
                if (delegateStorage is not (ILocalSymbol or IFieldSymbol or IPropertySymbol) ||
                    delegateStorage.DeclaringSyntaxReferences.IsDefaultOrEmpty)
                {
                    return $"the tuple returned from '{functionName}'";
                }

                referenceTarget = delegateStorage;
            }
            else if (IsOverridableOrInterfaceMember(functionSymbol))
            {
                return $"the tuple returned from '{functionName}'";
            }

            var references = await SymbolFinder.FindReferencesAsync(
                referenceTarget, currentDocument.Project.Solution, cancellationToken).ConfigureAwait(false);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.IsImplicit || location.IsCandidateLocation)
                        return $"the tuple returned from '{functionName}'";

                    var siteDocument = location.Document;
                    var siteRoot = await siteDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    var siteModel = await siteDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    if (siteRoot == null || siteModel == null)
                        return $"the tuple returned from '{functionName}'";

                    var siteNode = siteRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    if (siteNode is not ExpressionSyntax siteExpression)
                        return $"the tuple returned from '{functionName}'";

                    if (!TryGetCallSiteResult(
                            siteExpression,
                            calledAsDelegate: lambda != null,
                            isProperty: referenceTarget is IPropertySymbol && lambda == null,
                            out var result,
                            out var ignore))
                    {
                        return $"the tuple returned from '{functionName}'";
                    }

                    if (ignore)
                        continue;

                    pending.Push((siteDocument, siteRoot, siteModel, result!, functionName, sequence || isIterator));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Climbs one value-preserving level for opaque-return flow: parentheses,
    /// casts / <c>as</c>, null-forgiving, checked, await, conditional / coalesce /
    /// switch-expression branches, and storage-carrying containers.
    /// </summary>
    private static bool TryClimbOpaqueReturnFlow(ExpressionSyntax node, out ExpressionSyntax next)
    {
        next = null!;
        switch (node.Parent)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                next = parenthesized;
                return true;
            case CastExpressionSyntax cast when cast.Expression == node:
                next = cast;
                return true;
            case BinaryExpressionSyntax asExpression
                when asExpression.IsKind(SyntaxKind.AsExpression) && asExpression.Left == node:
                next = asExpression;
                return true;
            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                next = coalesce;
                return true;
            case PostfixUnaryExpressionSyntax postfix
                when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                next = postfix;
                return true;
            case CheckedExpressionSyntax checkedExpression:
                next = checkedExpression;
                return true;
            case AwaitExpressionSyntax awaitExpression:
                next = awaitExpression;
                return true;
            case ConditionalExpressionSyntax conditional
                when conditional.WhenTrue == node || conditional.WhenFalse == node:
                next = conditional;
                return true;
            case SwitchExpressionArmSyntax { Parent: SwitchExpressionSyntax switchExpression } arm
                when arm.Expression == node:
                next = switchExpression;
                return true;
        }

        if (TryClimbAssignedStorageContainer(node, out var container) &&
            container is ExpressionSyntax containerExpression)
        {
            next = containerExpression;
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>Bump(ref Get().Item1)</c> / <c>ref var r = ref Get().Item1</c> /
    /// <c>&amp;Get().Item1</c> (and, for the record gate, <c>Get().Item1 = 5</c> /
    /// increments) use an element of an opaque call result directly.
    /// </summary>
    private static bool DirectElementUseIsUnsafe(
        ExpressionSyntax value,
        HashSet<string> memberNames,
        bool includeWrites)
    {
        if (value.Parent is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Expression != value ||
            !memberNames.Contains(memberAccess.Name.Identifier.ValueText))
        {
            return false;
        }

        SyntaxNode use = memberAccess;
        while (use.Parent is ParenthesizedExpressionSyntax)
            use = use.Parent;

        switch (use.Parent)
        {
            case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
            case RefExpressionSyntax:
                return true;
            case PrefixUnaryExpressionSyntax addressOf when addressOf.IsKind(SyntaxKind.AddressOfExpression):
                return true;
            case AssignmentExpressionSyntax assignment when assignment.Left == use:
                return includeWrites || assignment.Right is RefExpressionSyntax;
            case { } parent when IsAssignmentOrIncrementTarget(parent):
                return includeWrites;
        }

        return false;
    }

    /// <summary>
    /// Resolves the function (method / local function / property / lambda) whose
    /// opaque, non-tuple return type receives <paramref name="value"/> through a
    /// <c>return</c> statement or expression body.
    /// </summary>
    private static bool TryGetOpaqueReturningFunction(
        ExpressionSyntax value,
        SemanticModel model,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out ISymbol? function,
        out AnonymousFunctionExpressionSyntax? lambda,
        out bool isIterator)
    {
        function = null;
        lambda = null;
        isIterator = false;

        SyntaxNode? owner = value.Parent switch
        {
            ReturnStatementSyntax returnStatement when returnStatement.Expression == value =>
                returnStatement.Ancestors().FirstOrDefault(a =>
                    a is AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax
                        or BaseMethodDeclarationSyntax
                        or AccessorDeclarationSyntax),
            ArrowExpressionClauseSyntax arrow when arrow.Expression == value => arrow.Parent,
            AnonymousFunctionExpressionSyntax anonymous when anonymous.ExpressionBody == value => anonymous,
            // yield return (1, 2) in an iterator method / local function / getter:
            // the call result is a sequence carrying the boxed tuple (Codex P1 on
            // #3441, PRRT_kwDORD0nYs605hQG). Iterators cannot be lambdas.
            YieldStatementSyntax yieldStatement
                when yieldStatement.IsKind(SyntaxKind.YieldReturnStatement) && yieldStatement.Expression == value =>
                yieldStatement.Ancestors().FirstOrDefault(a =>
                    a is AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax
                        or BaseMethodDeclarationSyntax
                        or AccessorDeclarationSyntax),
            _ => null
        };

        if (owner == null || owner is AnonymousFunctionExpressionSyntax && value.Parent is YieldStatementSyntax)
            return false;

        isIterator = value.Parent is YieldStatementSyntax;

        ISymbol? symbol = owner switch
        {
            AnonymousFunctionExpressionSyntax anonymous => model.GetSymbolInfo(anonymous, cancellationToken).Symbol,
            _ => model.GetDeclaredSymbol(owner, cancellationToken)
        };

        if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol associated } &&
            owner is AccessorDeclarationSyntax)
        {
            symbol = associated;
        }

        var returnType = symbol switch
        {
            _ when isIterator => symbol switch
            {
                IMethodSymbol method => TryGetIteratorElementType(method.ReturnType),
                IPropertySymbol property => TryGetIteratorElementType(property.Type),
                _ => null
            },
            IMethodSymbol method => method.IsAsync ? UnwrapAsyncReturnType(method.ReturnType) : method.ReturnType,
            IPropertySymbol property => property.Type,
            _ => null
        };

        if (returnType == null || returnType.TypeKind == TypeKind.Error || IsTupleLike(returnType))
            return false;

        if (symbol is IMethodSymbol { ReturnsVoid: true })
            return false;

        function = symbol!;
        lambda = owner as AnonymousFunctionExpressionSyntax;
        return true;
    }

    /// <summary>
    /// Element type yielded by an iterator returning <c>IEnumerable&lt;T&gt;</c> /
    /// <c>IEnumerator&lt;T&gt;</c> / <c>IAsyncEnumerable&lt;T&gt;</c> /
    /// <c>IAsyncEnumerator&lt;T&gt;</c>, or <c>object</c> for the non-generic
    /// <c>IEnumerable</c> / <c>IEnumerator</c> forms.
    /// </summary>
    private static ITypeSymbol? TryGetIteratorElementType(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named)
            return null;

        if (named.IsGenericType && named.TypeArguments.Length == 1 &&
            named.OriginalDefinition.SpecialType is SpecialType.System_Collections_Generic_IEnumerable_T
                or SpecialType.System_Collections_Generic_IEnumerator_T)
        {
            return named.TypeArguments[0];
        }

        if (named.IsGenericType && named.TypeArguments.Length == 1 &&
            named.Name is "IAsyncEnumerable" or "IAsyncEnumerator" &&
            named.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
        {
            return named.TypeArguments[0];
        }

        // Non-generic IEnumerable / IEnumerator: elements are object (Current).
        if (named.SpecialType == SpecialType.System_Collections_IEnumerator)
            return named.GetMembers("Current").OfType<IPropertySymbol>().FirstOrDefault()?.Type;

        if (named.SpecialType == SpecialType.System_Collections_IEnumerable)
        {
            var enumerator = named.GetMembers("GetEnumerator").OfType<IMethodSymbol>().FirstOrDefault()?.ReturnType;
            return enumerator?.GetMembers("Current").OfType<IPropertySymbol>().FirstOrDefault()?.Type;
        }

        return null;
    }

    private static ITypeSymbol? UnwrapAsyncReturnType(ITypeSymbol returnType) =>
        returnType is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
            ? named.TypeArguments[0]
            : null;

    private static bool IsOverridableOrInterfaceMember(ISymbol symbol)
    {
        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
            return true;

        if (symbol is IMethodSymbol { ExplicitInterfaceImplementations.IsDefaultOrEmpty: false } or
            IPropertySymbol { ExplicitInterfaceImplementations.IsDefaultOrEmpty: false })
        {
            return true;
        }

        var containingType = symbol.ContainingType;
        if (containingType == null)
            return false;

        foreach (var @interface in containingType.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers())
            {
                if (SymbolEqualityComparer.Default.Equals(
                        containingType.FindImplementationForInterfaceMember(member), symbol))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Maps a reference to an opaque-returning function (or the delegate storage
    /// holding an opaque-returning lambda) to the call result expression. Returns
    /// false for unresolvable uses (method groups, escaping delegates, conditional
    /// access); sets <paramref name="ignore"/> for <c>nameof</c> / delegate or
    /// property assignment targets.
    /// </summary>
    private static bool TryGetCallSiteResult(
        ExpressionSyntax reference,
        bool calledAsDelegate,
        bool isProperty,
        out ExpressionSyntax? result,
        out bool ignore)
    {
        result = null;
        ignore = false;

        ExpressionSyntax expression = reference;
        if (expression.Parent is MemberAccessExpressionSyntax qualified && qualified.Name == expression)
            expression = qualified;

        if (expression.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation =>
                invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } &&
                invocation.ArgumentList.Span.Contains(reference.Span)))
        {
            ignore = true;
            return true;
        }

        if (expression.Parent is AssignmentExpressionSyntax assignment &&
            assignment.Left == expression &&
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            // Writing the delegate / property: the value read elsewhere is tracked
            // at its own reference sites.
            ignore = true;
            return true;
        }

        if (isProperty)
        {
            result = expression;
            return true;
        }

        if (expression.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expression)
        {
            result = invocation;
            return true;
        }

        // get.Invoke() on a delegate holding the lambda.
        if (calledAsDelegate &&
            expression.Parent is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Invoke",
                Parent: InvocationExpressionSyntax invokeCall
            } invokeAccess &&
            invokeAccess.Expression == expression &&
            invokeCall.Expression == invokeAccess)
        {
            result = invokeCall;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fields / properties (and other non-local storage) may be used from another
    /// document of a partial type or from a referencing/dependent project; scan the
    /// originating project and every project that can already see it (same set as
    /// <see cref="CollectSameShapeCreationsAsync"/>) for aliases and by-ref uses
    /// (Codex P1 on #3441). Locals / parameters / range variables start document-scoped,
    /// but when <see cref="CollectStorageAliases"/> propagates a source-declared alias
    /// into another document (partial callee / constructor / method-group target), that
    /// declaring document is scanned too (Codex P1 on #3441, PRRT_kwDORD0nYs604Ela).
    /// </summary>
    private static bool RequiresCrossDocumentStorageScan(ISymbol storage) =>
        storage is IFieldSymbol or IPropertySymbol;

    /// <summary>
    /// The single escape / propagation routine shared by the record-init and by-ref
    /// gates. <paramref name="seedValue"/> (the selected creation, or an opaque-return
    /// call-site result) is registered as an alias-like value next to its assigned
    /// <paramref name="storage"/>, so <see cref="ExpressionReferencesStorageAlias"/>
    /// recognizes the value at its own use site. Every alias-propagation rule
    /// (setter / init / indexer value parameters, callee / constructor / stored-delegate
    /// / method-group / lambda parameters, user-defined operator and conversion
    /// parameters, collection inserts, generic wrappers, fields, …) and every
    /// conservative unresolved-callee refusal (extension receivers, interface /
    /// abstract / open-virtual dispatch, reassigned delegates, expanded
    /// <c>params</c>, dynamic binding) therefore applies identically to direct uses
    /// and alias uses (Codex P1s on #3441, PRRT_kwDORD0nYs605__Q / …605__Y /
    /// …605__c).
    /// </summary>
    private static async Task<bool> SeedValueHasUnsafeEscapeAsync(
        Document originatingDocument,
        SyntaxNode originatingRoot,
        SemanticModel originatingModel,
        ExpressionSyntax seedValue,
        ISymbol? storage,
        HashSet<string> memberNames,
        ITypeSymbol tupleType,
        bool includeWrites,
        CancellationToken cancellationToken)
    {
        var aliases = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (storage != null)
            aliases.Add(storage);
        RegisterSeedValue(aliases, seedValue);

        var documents = storage != null && RequiresCrossDocumentStorageScan(storage)
            ? await CollectCrossDocumentStorageAliasesAsync(
                originatingDocument, aliases, cancellationToken).ConfigureAwait(false)
            : await CollectPropagatedAliasDocumentsAsync(
                originatingDocument, originatingRoot, originatingModel, aliases, cancellationToken)
                .ConfigureAwait(false);

        foreach (var (root, model) in documents)
        {
            if (includeWrites
                    ? HasElementWrite(root, model, aliases, memberNames, tupleType)
                    : HasElementByRefUse(root, model, aliases, memberNames, tupleType))
            {
                return true;
            }

            // Conservative escape: converted values flowing into a callee we cannot
            // map to a single concrete analyzable target are treated like a by-ref
            // use / element write (Codex P1s on #3441 — reduced extension receivers,
            // virtual/interface dispatch, reassigned delegates, params expansion,
            // unknown callees, accessors, operators).
            if (HasUnresolvedCalleeEscape(root, model, aliases, cancellationToken))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Alias-like seed values (selected creations / opaque-return call-site results)
    /// attached to a specific alias set. Keyed by tree + span + kind rather than node
    /// identity so lookups survive red-node re-materialization.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HashSet<ISymbol>, List<(SyntaxTree Tree, TextSpan Span, int Kind)>>
        SeedValues = new();

    private static void RegisterSeedValue(HashSet<ISymbol> aliases, ExpressionSyntax seedValue)
    {
        var unwrapped = SkipTransparentWrappers(seedValue);
        SeedValues.GetOrCreateValue(aliases).Add((unwrapped.SyntaxTree, unwrapped.Span, unwrapped.RawKind));
    }

    private static bool IsSeedValue(HashSet<ISymbol> aliases, ExpressionSyntax expression)
    {
        if (!SeedValues.TryGetValue(aliases, out var seeds) || seeds.Count == 0)
            return false;

        foreach (var (tree, span, kind) in seeds)
        {
            if (expression.RawKind == kind && expression.Span == span && expression.SyntaxTree == tree)
                return true;
        }

        return false;
    }

    /// <summary>
    /// For document-scoped storage (locals / parameters / range variables / bare
    /// seed values), propagates aliases in the originating document and then
    /// continues into every syntax tree that declares a collected alias — e.g. a
    /// callee / constructor / accessor / operator parameter whose body lives in
    /// another partial file or project (Codex P1 on #3441, PRRT_kwDORD0nYs604Ela).
    /// Mutates <paramref name="aliases"/> in place so seed values stay attached.
    /// </summary>
    private static async Task<List<(SyntaxNode Root, SemanticModel Model)>>
        CollectPropagatedAliasDocumentsAsync(
            Document originatingDocument,
            SyntaxNode originatingRoot,
            SemanticModel originatingModel,
            HashSet<ISymbol> aliases,
            CancellationToken cancellationToken)
    {
        PropagateStorageAliases(originatingRoot, originatingModel, aliases, cancellationToken);
        var documents = new List<(SyntaxNode Root, SemanticModel Model)>
        {
            (originatingRoot, originatingModel)
        };
        var loadedTrees = new HashSet<SyntaxTree> { originatingRoot.SyntaxTree };

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var symbol in aliases.ToArray())
            {
                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    var tree = reference.SyntaxTree;
                    if (!loadedTrees.Add(tree))
                        continue;

                    var document = originatingDocument.Project.Solution.GetDocument(tree);
                    if (document == null)
                        continue;

                    var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    if (root == null || model == null)
                        continue;

                    documents.Add((root, model));
                    changed = true;
                }
            }

            // Re-propagate each loaded document so copies inside newly discovered
            // callee bodies (and further callees they invoke) join the alias set.
            foreach (var (root, model) in documents)
            {
                var before = aliases.Count;
                PropagateStorageAliases(root, model, aliases, cancellationToken);
                if (aliases.Count != before)
                    changed = true;
            }
        }

        return documents;
    }

    /// <summary>
    /// Fixpoint-propagates <paramref name="aliases"/> across every document in the
    /// originating project (partial types / sibling files) and in referencing /
    /// dependent projects the refactoring already rewrites
    /// (<see cref="ProjectCanReference"/>), then returns the loaded roots+models for
    /// by-ref / write checks (Codex P1 on #3441).
    /// </summary>
    private static async Task<List<(SyntaxNode Root, SemanticModel Model)>>
        CollectCrossDocumentStorageAliasesAsync(
            Document originatingDocument,
            HashSet<ISymbol> aliases,
            CancellationToken cancellationToken)
    {
        var loaded = new List<(SyntaxNode Root, SemanticModel Model)>();
        var originatingProject = originatingDocument.Project;

        foreach (var project in originatingProject.Solution.Projects)
        {
            if (!ProjectCanReference(project, originatingProject))
                continue;

            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root == null || model == null)
                    continue;
                loaded.Add((root, model));
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (root, model) in loaded)
            {
                var before = aliases.Count;
                PropagateStorageAliases(root, model, aliases, cancellationToken);
                if (aliases.Count != before)
                    changed = true;
            }
        }

        return loaded;
    }

    /// <summary>
    /// Resolves the local/parameter/field/property that a convertible creation
    /// initializes. Walks through nested array/collection/object/anonymous/tuple
    /// containers so <c>dynamic[] points = { (1, 2) }</c> tracks <c>points</c>
    /// rather than returning null when the tuple's parent is an initializer
    /// (Codex P1 on #3441).
    /// </summary>
    internal static ISymbol? TryGetAssignedStorageSymbol(
        ExpressionSyntax creation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken = default)
    {
        SyntaxNode node = creation;
        while (true)
        {
            while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
                node = parenthesized;

            // (1, 2)! inside an initializer — climb through null-forgiving.
            while (node.Parent is PostfixUnaryExpressionSyntax postfix &&
                   postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                node = postfix;
            }

            // checked((1, 2)) / unchecked((1, 2)) inside a container.
            while (node.Parent is CheckedExpressionSyntax checkedExpression)
                node = checkedExpression;

            if (node.Parent is EqualsValueClauseSyntax equals && equals.Value == node)
            {
                switch (equals.Parent)
                {
                    case VariableDeclaratorSyntax declarator:
                        return semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
                    case PropertyDeclarationSyntax property:
                        return semanticModel.GetDeclaredSymbol(property, cancellationToken);
                }

                return null;
            }

            if (node.Parent is AssignmentExpressionSyntax assignment &&
                assignment.Right == node)
            {
                // Object / with initializers assign a member; keep climbing to the
                // enclosing storage (new Box { Value = (1, 2) } → box).
                if (assignment.Parent is InitializerExpressionSyntax)
                {
                    node = assignment;
                    continue;
                }

                return semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
            }

            // Use((X: 1, Y: 2)) — the matching callee parameter is the storage for a
            // nested creation passed directly as an ordinary argument (sibling of
            // constructor-arg climb; Codex P1 on #3441, PRRT_kwDORD0nYs603sqw).
            if (TryGetInvocationArgumentParameterStorage(
                    node, semanticModel, cancellationToken, out var parameterStorage))
            {
                return parameterStorage;
            }

            if (!TryClimbAssignedStorageContainer(node, out var container))
                return null;

            node = container;
        }
    }

    /// <summary>
    /// When a nested convertible creation is passed directly to an ordinary
    /// invocation / local function / delegate (<c>Use((X: 1, Y: 2))</c>), resolves
    /// the matching callee parameter as the creation's storage so by-ref uses
    /// through that parameter are still gated.
    /// </summary>
    private static bool TryGetInvocationArgumentParameterStorage(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ISymbol? parameterStorage)
    {
        parameterStorage = null;
        if (node.Parent is not ArgumentSyntax argument ||
            argument.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } argumentList)
        {
            return false;
        }

        if (!TryResolveInvocationMethod(invocation, semanticModel, cancellationToken, out var method) ||
            method == null)
        {
            return false;
        }

        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0)
            return false;

        var parameter = ResolveInvocationParameter(method, argument, index);
        if (parameter == null)
            return false;

        // Delegate Invoke parameters are synthesized; prefer the source lambda /
        // method-group parameter that the stored delegate was assigned from.
        if (method.MethodKind == MethodKind.DelegateInvoke)
        {
            var mapped = TryMapStoredDelegateParameter(
                invocation, method, index, semanticModel, cancellationToken);
            if (mapped != null)
            {
                parameterStorage = mapped;
                return true;
            }
        }

        parameterStorage = parameter.OriginalDefinition;
        return true;
    }

    /// <summary>
    /// Climbs one nesting level from a nested convertible creation (or an
    /// intermediate container already climbed into) toward enclosing storage.
    /// Walks value-preserving initializer / collection / aggregate wrappers and
    /// opaque constructor arguments (<c>new Holder((X: 1, Y: 2))</c> → holder) —
    /// not ordinary transforming invocations (Codex P1 on #3441).
    /// </summary>
    private static bool TryClimbAssignedStorageContainer(
        SyntaxNode node,
        out SyntaxNode container)
    {
        container = null!;
        var parent = node.Parent;
        if (parent == null)
            return false;

        // Array / collection / object / complex-element initializers.
        if (parent is InitializerExpressionSyntax)
        {
            container = parent;
            return true;
        }

        // new[] { … } / new T[] { … } / stackalloc / new List<> { … } / new { }
        // that own an initializer we already climbed into.
        if (node is InitializerExpressionSyntax &&
            parent is ExpressionSyntax owningExpression &&
            IsStorageCarryingContainerExpression(owningExpression))
        {
            container = owningExpression;
            return true;
        }

        // Collection expressions: [(1, 2)] / [..(other), (1, 2)].
        if (parent is ExpressionElementSyntax { Parent: CollectionExpressionSyntax collection })
        {
            container = collection;
            return true;
        }

        // Spread elements: [.. points] / [.. new[] { (1, 2) }] (Codex P1 on #3441).
        if (parent is SpreadElementSyntax { Parent: CollectionExpressionSyntax spreadCollection })
        {
            container = spreadCollection;
            return true;
        }

        // Nested tuples: ((1, 2), 0) / (Value: (1, 2), Other: 0).
        if (parent is ArgumentSyntax { Parent: TupleExpressionSyntax tuple })
        {
            container = tuple;
            return true;
        }

        // Anonymous members: new { Value = (1, 2) }.
        if (parent is AnonymousObjectMemberDeclaratorSyntax
            {
                Parent: AnonymousObjectCreationExpressionSyntax anonymous
            })
        {
            container = anonymous;
            return true;
        }

        // Direct constructor arguments: new Holder((X: 1, Y: 2)) / new((X: 1, Y: 2))
        // climb to the object creation so the enclosing assigned carrier (holder) is
        // resolved instead of skipping validation (Codex P1 on #3441,
        // PRRT_kwDORD0nYs603sqr).
        if (parent is ArgumentSyntax
            {
                Parent: ArgumentListSyntax { Parent: BaseObjectCreationExpressionSyntax objectCreation }
            })
        {
            container = objectCreation;
            return true;
        }

        return false;
    }

    private static bool IsStorageCarryingContainerExpression(ExpressionSyntax expression) =>
        expression is ImplicitArrayCreationExpressionSyntax
            or ArrayCreationExpressionSyntax
            or ImplicitStackAllocArrayCreationExpressionSyntax
            or StackAllocArrayCreationExpressionSyntax
            or BaseObjectCreationExpressionSyntax
            or AnonymousObjectCreationExpressionSyntax;

    /// <summary>
    /// Collects <paramref name="storage"/> plus locals/parameters/fields that are
    /// assigned from it (value copies), so element writes through aliases are rejected.
    /// </summary>
    internal static HashSet<ISymbol> CollectStorageAliases(
        SyntaxNode root,
        SemanticModel semanticModel,
        ISymbol storage,
        CancellationToken cancellationToken = default)
    {
        var aliases = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { storage };
        PropagateStorageAliases(root, semanticModel, aliases, cancellationToken);
        return aliases;
    }

    /// <summary>
    /// In-place alias propagation over one document. Shared by symbol-seeded
    /// storage and alias-like seed values (<see cref="RegisterSeedValue"/>), so a
    /// selected creation used directly flows through exactly the same rules as a
    /// named alias (Codex P1s on #3441, PRRT_kwDORD0nYs605__Q / …605__Y / …605__c).
    /// </summary>
    private static void PropagateStorageAliases(
        SyntaxNode root,
        SemanticModel semanticModel,
        HashSet<ISymbol> aliases,
        CancellationToken cancellationToken)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in root.DescendantNodes())
            {
                ISymbol? alias = null;
                ExpressionSyntax? source = null;

                // holder.Value = point / holder[i] = point / holder[point]:
                // property / indexer accessor parameters (setter `value`, indexer
                // index parameters) receive converted storage (Codex P1 on #3441,
                // PRRT_kwDORD0nYs605hQL). consumer + point / (Wrapper)point:
                // user-defined operator / conversion parameters likewise (Codex P1
                // on #3441, PRRT_kwDORD0nYs605hQM). No `continue`: the ordinary
                // branches below still run for the same node.
                if (TryAddAccessorParameterAliases(node, aliases, semanticModel, cancellationToken) |
                    TryAddUserDefinedOperatorParameterAliases(node, aliases, semanticModel, cancellationToken))
                {
                    changed = true;
                }

                if (node is VariableDeclaratorSyntax declarator &&
                    declarator.Initializer?.Value is { } init)
                {
                    alias = semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
                    source = init;
                }
                else if (node is AssignmentExpressionSyntax assignment &&
                         (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                          assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)))
                {
                    // var (copy, _) = (point, 0); / (copy, _) = … / (var copy, var _) = …
                    // Also copy ??= point (Codex P1 on #3441).
                    if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                        TryAddDeconstructionAliasesFromAssignment(
                            assignment.Left,
                            assignment.Right,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken))
                    {
                        changed = true;
                        continue;
                    }

                    // list[0] = point / map[key] = point: the indexed container
                    // carries converted storage, not just the indexer symbol.
                    // Also list[0] ??= point for coalesce assignment.
                    if (UnwrapParenthesesAndNullForgiving(assignment.Left) is ElementAccessExpressionSyntax
                        {
                            Expression: { } indexedContainer
                        } &&
                        TryAddContainerReceiverAlias(
                            indexedContainer,
                            assignment.Right,
                            aliases,
                            semanticModel,
                            cancellationToken))
                    {
                        changed = true;
                        continue;
                    }

                    alias = semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
                    source = assignment.Right;
                }
                else if (node is ForEachStatementSyntax forEach)
                {
                    // foreach (ref var p in points.AsSpan()) p.X = 3;
                    // foreach (var copy in points) Bump(ref copy.X); (Codex P1)
                    alias = semanticModel.GetDeclaredSymbol(forEach, cancellationToken);
                    source = forEach.Expression;
                }
                else if (node is ForEachVariableStatementSyntax forEachVariable)
                {
                    // foreach (var (copy, _) in new[] { (point, 0) }) Bump(ref copy.X);
                    // Deconstructing foreach never enters ForEachStatementSyntax (Codex P1).
                    if (ExpressionReferencesStorageAlias(
                            forEachVariable.Expression,
                            aliases,
                            semanticModel,
                            cancellationToken) &&
                        TryAddForeachVariableDesignationAliases(
                            forEachVariable.Variable,
                            aliases,
                            semanticModel,
                            cancellationToken))
                    {
                        changed = true;
                        continue;
                    }
                }
                else if (node is IsPatternExpressionSyntax isPattern)
                {
                    // if (point is var copy) / point is { } copy (Codex P1)
                    if (TryAddPatternDesignationAliases(
                            isPattern.Pattern,
                            isPattern.Expression,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken))
                    {
                        changed = true;
                        continue;
                    }
                }
                else if (node is SwitchExpressionArmSyntax arm &&
                         arm.Parent is SwitchExpressionSyntax switchExpression)
                {
                    if (TryAddPatternDesignationAliases(
                            arm.Pattern,
                            switchExpression.GoverningExpression,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken))
                    {
                        changed = true;
                        continue;
                    }
                }
                else if (node is CasePatternSwitchLabelSyntax caseLabel &&
                         caseLabel.Parent?.Parent is SwitchStatementSyntax switchStatement)
                {
                    if (TryAddPatternDesignationAliases(
                            caseLabel.Pattern,
                            switchStatement.Expression,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken))
                    {
                        changed = true;
                        continue;
                    }
                }
                else if (node is FromClauseSyntax fromClause)
                {
                    // from copy in new[] { point } select Read(ref copy.X) (Codex P1)
                    alias = semanticModel.GetDeclaredSymbol(fromClause, cancellationToken);
                    source = fromClause.Expression;
                }
                else if (node is LetClauseSyntax letClause)
                {
                    // let copy = point (Codex P1)
                    alias = semanticModel.GetDeclaredSymbol(letClause, cancellationToken);
                    source = letClause.Expression;
                }
                else if (node is JoinClauseSyntax joinClause)
                {
                    // join copy in new[] { point } on ... (Codex P1)
                    alias = semanticModel.GetDeclaredSymbol(joinClause, cancellationToken);
                    source = joinClause.InExpression;
                }
                else if (node is JoinIntoClauseSyntax joinInto &&
                         joinInto.Parent is JoinClauseSyntax parentJoin)
                {
                    // join ... into group (Codex P1)
                    alias = semanticModel.GetDeclaredSymbol(joinInto, cancellationToken);
                    source = parentJoin.InExpression;
                }
                else if (node is QueryContinuationSyntax continuation &&
                         continuation.Parent is QueryBodySyntax queryBody)
                {
                    // ... select x into copy (Codex P1)
                    alias = semanticModel.GetDeclaredSymbol(continuation, cancellationToken);
                    source = queryBody.SelectOrGroup switch
                    {
                        SelectClauseSyntax select => select.Expression,
                        GroupClauseSyntax group => group.GroupExpression,
                        _ => null
                    };
                }
                else if (node is LocalFunctionStatementSyntax localFunction)
                {
                    // (int X, int Y) Get() => point; var copy = Get(); (Codex P1 on #3441)
                    var localSymbol = semanticModel.GetDeclaredSymbol(localFunction, cancellationToken);
                    if (localSymbol != null &&
                        !aliases.Contains(localSymbol) &&
                        LocalFunctionReturnsConvertedStorage(
                            localFunction, aliases, semanticModel, cancellationToken))
                    {
                        aliases.Add(localSymbol);
                        changed = true;
                        continue;
                    }
                }
                else if (node is InvocationExpressionSyntax invocation)
                {
                    // Array.ForEach / List<T>.ForEach / Copy(point, out var copy) (Codex P1)
                    var lambdaAdded = TryAddLambdaParameterAliasesFromInvocation(
                        invocation, aliases, semanticModel, root, cancellationToken);
                    var outVarAdded = TryAddOutVarAliasesFromInvocation(
                        invocation, aliases, semanticModel, root, cancellationToken);
                    // list.Add(point) / queue.Enqueue(point): the receiver now carries
                    // converted storage (read back via ToArray/indexer/foreach).
                    var insertionAdded = TryAddCollectionInsertionReceiverAlias(
                        invocation, aliases, semanticModel, cancellationToken);
                    // Use(point) / storedDelegate(point): ordinary callee / stored-delegate
                    // parameters receiving converted storage (Codex P1 on #3441,
                    // PRRT_kwDORD0nYs603sqw).
                    var calleeAdded = TryAddOrdinaryCalleeParameterAliasesFromInvocation(
                        invocation, aliases, semanticModel, root, cancellationToken);
                    if (lambdaAdded || outVarAdded || insertionAdded || calleeAdded)
                    {
                        changed = true;
                        continue;
                    }
                }
                else if (node is BaseObjectCreationExpressionSyntax objectCreation)
                {
                    // new Holder(point): ordinary constructor parameters receiving
                    // converted storage (sibling of invocation callees; bodies may live
                    // in another partial document — Codex P1 on #3441, PRRT_kwDORD0nYs604Ela).
                    if (TryAddOrdinaryCalleeParameterAliasesFromObjectCreation(
                            objectCreation, aliases, semanticModel, cancellationToken))
                    {
                        changed = true;
                        continue;
                    }
                }
                else if (node is ConstructorInitializerSyntax constructorInitializer)
                {
                    // Holder(object value) : this(value, 0) / : base(value) — forward a
                    // tracked constructor parameter into the destination ctor parameter
                    // so by-ref uses in the chained body are gated (Codex P1 on #3441,
                    // PRRT_kwDORD0nYs606U0Z).
                    if (TryAddOrdinaryCalleeParameterAliasesFromConstructorInitializer(
                            constructorInitializer, aliases, semanticModel, cancellationToken))
                    {
                        changed = true;
                        continue;
                    }
                }

                if (alias == null || source == null || aliases.Contains(alias))
                    continue;

                while (true)
                {
                    if (TryUnwrapTransparentWrapper(source, out var unwrappedSource))
                        source = unwrappedSource;
                    else if (source is RefExpressionSyntax refExpression)
                        source = refExpression.Expression;
                    // Pointer aliases: var p = &point; / p = &point (Codex P1).
                    else if (source is PrefixUnaryExpressionSyntax addressOf &&
                             addressOf.IsKind(SyntaxKind.AddressOfExpression))
                        source = addressOf.Operand;
                    // Pointer-derived aliases: var q = p + 0 / p - 0 / ++p / p++ /
                    // (T*)p / (p += 0) (Codex P1 on #3441).
                    else if (TryUnwrapPointerDerivedSource(source, semanticModel, out var pointerOperand))
                        source = pointerOperand;
                    // Built-in identity/boxing casts / as: dynamic copy = point as object (Codex P1).
                    else if (TryUnwrapValuePreservingCast(
                                 source, semanticModel, cancellationToken, out var castOperand))
                        source = castOperand;
                    else
                        break;
                }

                if (ExpressionReferencesStorageAlias(source, aliases, semanticModel, cancellationToken) ||
                    // var copy = box.Value / (point, 0).Item1 / box!.Inner.Value — member
                    // reads of aggregates that embed converted storage (Codex P1 on #3441).
                    (TryResolveAggregateMemberRead(
                         source, aliases, semanticModel, root, cancellationToken, out var memberSource) &&
                     ExpressionReferencesStorageAlias(memberSource, aliases, semanticModel, cancellationToken)))
                {
                    aliases.Add(alias);
                    changed = true;
                }
            }
        }

    }

    /// <summary>
    /// <c>list.Add(point)</c> / <c>list.Insert(0, point)</c> / <c>queue.Enqueue(point)</c>:
    /// marks the receiver as carrying converted storage so reads back through
    /// <c>ToArray()</c>, indexers, or <c>foreach</c> stay gated (refusal-leaning).
    /// </summary>
    private static bool TryAddCollectionInsertionReceiverAlias(
        InvocationExpressionSyntax invocation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Add" or "AddRange" or "Insert" or "InsertRange"
                    or "Push" or "Enqueue" or "TryAdd" or "AddFirst" or "AddLast"
            } memberAccess)
        {
            return false;
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
            (method.ReducedFrom == null && method.IsStatic))
        {
            return false;
        }

        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            var parameter = ResolveInvocationParameter(method, argument, i);
            if (parameter == null)
                continue;

            // Only taint when the argument flows into a parameter whose type
            // embeds converted storage. Dictionary<object, (int X, int Y)>.Add(point, …)
            // must not mark the receiver via the unrelated object key (Codex P2 on #3441).
            // Opaque element collections (List<object> / ArrayList / Queue<dynamic>)
            // box the converted value and hand it back as object / dynamic, so
            // they are carriers too (Codex P1s on #3441, PRRT_kwDORD0nYs605__Y audit:
            // list.Add((1, 2)); dynamic copy = list[0]; Bump(ref copy.Item1);).
            if (!TypeInvolvesStorageAliasType(parameter.Type, aliases) &&
                !(IsOpaqueCarrierType(parameter.Type) &&
                  ReceiverElementTypesAreOpaque(memberAccess.Expression, semanticModel, cancellationToken)))
            {
                continue;
            }

            if (TryAddContainerReceiverAlias(
                    memberAccess.Expression,
                    argument.Expression,
                    aliases,
                    semanticModel,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOpaqueCarrierType(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType ||
        type.TypeKind is TypeKind.Dynamic or TypeKind.Interface;

    /// <summary>
    /// True when every type argument of the receiver's collection type is opaque
    /// (<c>List&lt;object&gt;</c>, <c>Dictionary&lt;object, dynamic&gt;</c>) or the
    /// collection is non-generic (<c>ArrayList</c>), so values read back are the boxed
    /// converted instances rather than an unrelated element type.
    /// </summary>
    private static bool ReceiverElementTypesAreOpaque(
        ExpressionSyntax receiver,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        semanticModel.GetTypeInfo(receiver, cancellationToken).Type is INamedTypeSymbol named &&
        named.TypeArguments.All(IsOpaqueCarrierType);

    /// <summary>
    /// Adds the symbol behind <paramref name="container"/> as an alias when
    /// <paramref name="value"/> embeds converted storage.
    /// </summary>
    private static bool TryAddContainerReceiverAlias(
        ExpressionSyntax container,
        ExpressionSyntax value,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        while (TryUnwrapTransparentWrapper(value, out var unwrappedValue))
            value = unwrappedValue;

        if (!ExpressionReferencesStorageAlias(value, aliases, semanticModel, cancellationToken))
            return false;

        container = UnwrapParenthesesAndNullForgiving(container);
        var receiver = semanticModel.GetSymbolInfo(container, cancellationToken).Symbol;
        if (receiver is not (ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol) ||
            aliases.Contains(receiver))
        {
            return false;
        }

        aliases.Add(receiver);
        return true;
    }

    /// <summary>
    /// Adds aliases for deconstruction destinations on an assignment LHS
    /// (<c>(copy, _) = (point, 0)</c> or <c>(var copy, var _) = (point, 0)</c>).
    /// </summary>
    private static bool TryAddDeconstructionAliasesFromAssignment(
        ExpressionSyntax left,
        ExpressionSyntax right,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        left = UnwrapParenthesesAndNullForgiving(left);

        if (left is DeclarationExpressionSyntax declaration &&
            declaration.Designation != null)
        {
            return TryAddDeconstructionAliases(
                declaration.Designation,
                right,
                aliases,
                semanticModel,
                root,
                cancellationToken);
        }

        if (left is TupleExpressionSyntax leftTuple)
        {
            if (!TryGetTupleElementExpressions(
                    right,
                    leftTuple.Arguments.Count,
                    semanticModel,
                    root,
                    cancellationToken,
                    out var elementSources) ||
                elementSources == null)
            {
                // (var copy, var other) = Wrap(point) — no literal elements (Codex P1).
                return TryAddOpaqueDeconstructionSourceAliases(
                    leftTuple, right, aliases, semanticModel, cancellationToken);
            }

            var added = false;
            for (var i = 0; i < leftTuple.Arguments.Count; i++)
            {
                var dest = leftTuple.Arguments[i].Expression;
                var src = elementSources[i];

                if (dest is DeclarationExpressionSyntax nestedDeclaration &&
                    nestedDeclaration.Designation != null)
                {
                    if (TryAddDeconstructionAliases(
                            nestedDeclaration.Designation,
                            src,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken))
                    {
                        added = true;
                    }

                    continue;
                }

                dest = UnwrapParenthesesAndNullForgiving(dest);
                var destSymbol = semanticModel.GetSymbolInfo(dest, cancellationToken).Symbol;
                if (destSymbol == null || aliases.Contains(destSymbol))
                    continue;

                if (ExpressionReferencesStorageAlias(src, aliases, semanticModel, cancellationToken))
                {
                    aliases.Add(destSymbol);
                    added = true;
                }
            }

            return added;
        }

        return false;
    }

    /// <summary>
    /// Pairs deconstruction destinations with corresponding RHS tuple elements
    /// so <c>var (copy, _) = (point, 0)</c> tracks <c>copy</c> as a storage alias.
    /// Also resolves tuple-typed expressions (aliases / locals), not only literal
    /// <see cref="TupleExpressionSyntax"/> RHS values (Codex P1).
    /// </summary>
    private static bool TryAddDeconstructionAliases(
        VariableDesignationSyntax designation,
        ExpressionSyntax source,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        source = UnwrapParenthesesAndRef(source);

        switch (designation)
        {
            case SingleVariableDesignationSyntax single:
                {
                    var alias = semanticModel.GetDeclaredSymbol(single, cancellationToken);
                    if (alias == null || aliases.Contains(alias))
                        return false;

                    if (!ExpressionReferencesStorageAlias(source, aliases, semanticModel, cancellationToken))
                        return false;

                    aliases.Add(alias);
                    return true;
                }
            case ParenthesizedVariableDesignationSyntax parenthesized:
                {
                    if (!TryGetTupleElementExpressions(
                            source,
                            parenthesized.Variables.Count,
                            semanticModel,
                            root,
                            cancellationToken,
                            out var elementSources) ||
                        elementSources == null)
                    {
                        // var (copy, _) = Wrap(point) — generic call result (Codex P1).
                        return TryAddOpaqueDeconstructionSourceAliases(
                            parenthesized, source, aliases, semanticModel, cancellationToken);
                    }

                    var added = false;
                    for (var i = 0; i < parenthesized.Variables.Count; i++)
                    {
                        if (TryAddDeconstructionAliases(
                                parenthesized.Variables[i],
                                elementSources[i],
                                aliases,
                                semanticModel,
                                root,
                                cancellationToken))
                        {
                            added = true;
                        }
                    }

                    return added;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves the per-element expressions of a deconstruction / positional-pattern
    /// source: a literal tuple, or the tuple initializer of a tuple-typed expression
    /// (e.g. <c>var wrapped = (point, 0); var (copy, _) = wrapped</c>).
    /// </summary>
    private static bool TryGetTupleElementExpressions(
        ExpressionSyntax source,
        int expectedCount,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken,
        out ExpressionSyntax[]? elements)
    {
        elements = null;
        source = UnwrapParenthesesAndRef(source);

        if (source is TupleExpressionSyntax tuple)
        {
            if (tuple.Arguments.Count != expectedCount)
                return false;

            elements = new ExpressionSyntax[expectedCount];
            for (var i = 0; i < expectedCount; i++)
                elements[i] = tuple.Arguments[i].Expression;
            return true;
        }

        var symbol = semanticModel.GetSymbolInfo(source, cancellationToken).Symbol;
        if (symbol == null)
            return false;

        if (!TryFindSymbolInitializerExpression(symbol, root, semanticModel, out var initializer) ||
            initializer == null)
        {
            return false;
        }

        // Chained aliases: var a = (point, 0); var b = a; var (copy, _) = b;
        return TryGetTupleElementExpressions(
            initializer, expectedCount, semanticModel, root, cancellationToken, out elements);
    }

    /// <summary>
    /// Fallback for deconstruction / positional-pattern sources without literal
    /// tuple elements to pair with — for example a generic call result
    /// (<c>var (copy, _) = Wrap(point)</c> with
    /// <c>static (T Value, int Other) Wrap&lt;T&gt;(T value)</c>) or a local
    /// initialized from one. When <paramref name="source"/> carries converted
    /// storage, every destination local whose declared type embeds a tracked
    /// storage type is treated as an alias (conservative — Codex P1 on #3441).
    /// </summary>
    private static bool TryAddOpaqueDeconstructionSourceAliases(
        SyntaxNode destinations,
        ExpressionSyntax source,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!ExpressionReferencesStorageAlias(source, aliases, semanticModel, cancellationToken))
            return false;

        var candidates = new List<ISymbol>();
        foreach (var node in destinations.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case SingleVariableDesignationSyntax single:
                    if (semanticModel.GetDeclaredSymbol(single, cancellationToken) is { } declared)
                        candidates.Add(declared);
                    break;
                // (copy, other) = Wrap(point) with pre-declared locals.
                case ArgumentSyntax { Expression: IdentifierNameSyntax identifier }
                    when node.Parent is TupleExpressionSyntax:
                    if (semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol is
                    { } assigned and (ILocalSymbol or IParameterSymbol or IFieldSymbol))
                    {
                        candidates.Add(assigned);
                    }

                    break;
            }
        }

        var added = false;
        foreach (var candidate in candidates)
        {
            if (aliases.Contains(candidate))
                continue;

            var type = GetAliasType(candidate);
            if (type == null || !TypeEmbedsStorageAliasType(type, aliases))
                continue;

            aliases.Add(candidate);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// True when <paramref name="type"/> is or contains (array element, type
    /// argument, tuple element) the type of a tracked storage alias.
    /// </summary>
    private static bool TypeEmbedsStorageAliasType(ITypeSymbol type, HashSet<ISymbol> aliases)
    {
        foreach (var alias in aliases)
        {
            var aliasType = GetAliasType(alias);
            if (aliasType != null && TypeContainsOrEquals(type, aliasType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Adds aliases for pattern designations that copy converted storage
    /// (<c>if (point is var copy)</c>, <c>point is { } copy</c>, switch arms/cases),
    /// including nested recursive-pattern designations such as
    /// <c>new { Value = point } is { Value: var copy }</c>, and list-pattern
    /// element designations such as <c>points is [var copy]</c> (Codex P1).
    /// </summary>
    private static bool TryAddPatternDesignationAliases(
        PatternSyntax pattern,
        ExpressionSyntax input,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        pattern = UnwrapParenthesizedPattern(pattern);

        switch (pattern)
        {
            case VarPatternSyntax varPattern:
                return TryAddDeconstructionAliases(
                    varPattern.Designation,
                    input,
                    aliases,
                    semanticModel,
                    root,
                    cancellationToken);

            case DeclarationPatternSyntax declarationPattern when
                declarationPattern.Designation != null:
                return TryAddDeconstructionAliases(
                    declarationPattern.Designation,
                    input,
                    aliases,
                    semanticModel,
                    root,
                    cancellationToken);

            case RecursivePatternSyntax recursive:
                {
                    var added = false;

                    if (recursive.Designation != null &&
                        TryAddDeconstructionAliases(
                            recursive.Designation,
                            input,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken))
                    {
                        added = true;
                    }

                    // Nested property subpatterns: { Value: var copy }
                    if (recursive.PropertyPatternClause != null)
                    {
                        foreach (var subpattern in recursive.PropertyPatternClause.Subpatterns)
                        {
                            // { Value: var copy } (NameColon) and extended property
                            // patterns { Inner.Value: var copy } (ExpressionColon) —
                            // resolve every member-path component (Codex P1 on #3441).
                            if (!TryGetPropertySubpatternMemberPath(subpattern, out var memberPath))
                                continue;

                            ExpressionSyntax? memberInput = input;
                            foreach (var memberName in memberPath)
                            {
                                if (!TryResolvePatternMemberInput(
                                        memberInput,
                                        memberName,
                                        aliases,
                                        semanticModel,
                                        root,
                                        cancellationToken,
                                        out memberInput) ||
                                    memberInput == null)
                                {
                                    memberInput = null;
                                    break;
                                }
                            }

                            if (memberInput == null)
                                continue;

                            if (TryAddPatternDesignationAliases(
                                    subpattern.Pattern,
                                    memberInput,
                                    aliases,
                                    semanticModel,
                                    root,
                                    cancellationToken))
                            {
                                added = true;
                            }
                        }
                    }

                    // Nested positional subpatterns: (var copy, _)
                    if (recursive.PositionalPatternClause != null)
                    {
                        var subpatterns = recursive.PositionalPatternClause.Subpatterns;
                        if (TryGetTupleElementExpressions(
                                input,
                                subpatterns.Count,
                                semanticModel,
                                root,
                                cancellationToken,
                                out var elementSources) &&
                            elementSources != null)
                        {
                            for (var i = 0; i < subpatterns.Count; i++)
                            {
                                if (TryAddPatternDesignationAliases(
                                        subpatterns[i].Pattern,
                                        elementSources[i],
                                        aliases,
                                        semanticModel,
                                        root,
                                        cancellationToken))
                                {
                                    added = true;
                                }
                            }
                        }
                        else if (TryAddOpaqueDeconstructionSourceAliases(
                                     recursive.PositionalPatternClause,
                                     input,
                                     aliases,
                                     semanticModel,
                                     cancellationToken))
                        {
                            // Wrap(point) is (var copy, _) — generic call result (Codex P1).
                            added = true;
                        }
                    }

                    return added;
                }

            case ListPatternSyntax listPattern:
                return TryAddListPatternDesignationAliases(
                    listPattern, input, aliases, semanticModel, root, cancellationToken);

            case BinaryPatternSyntax binary:
                {
                    var left = TryAddPatternDesignationAliases(
                        binary.Left, input, aliases, semanticModel, root, cancellationToken);
                    var right = TryAddPatternDesignationAliases(
                        binary.Right, input, aliases, semanticModel, root, cancellationToken);
                    return left || right;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Walks list-pattern element designations (<c>points is [var copy]</c>) and
    /// optional slice subpatterns, pairing elements with container initializers
    /// when possible (Codex P1).
    /// </summary>
    private static bool TryAddListPatternDesignationAliases(
        ListPatternSyntax listPattern,
        ExpressionSyntax input,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        var added = false;

        if (listPattern.Designation != null &&
            TryAddDeconstructionAliases(
                listPattern.Designation,
                input,
                aliases,
                semanticModel,
                root,
                cancellationToken))
        {
            added = true;
        }

        var patterns = listPattern.Patterns;
        var hasSlice = false;
        foreach (var pattern in patterns)
        {
            if (UnwrapParenthesizedPattern(pattern) is SlicePatternSyntax)
            {
                hasSlice = true;
                break;
            }
        }

        ExpressionSyntax[]? elementSources = null;
        var hasElementSources = !hasSlice &&
            TryGetListPatternElementSources(
                input,
                patterns.Count,
                semanticModel,
                root,
                cancellationToken,
                out elementSources);

        for (var i = 0; i < patterns.Count; i++)
        {
            var subpattern = UnwrapParenthesizedPattern(patterns[i]);

            if (subpattern is SlicePatternSyntax slice)
            {
                // Slice binds a subsequence of the container; nested pattern
                // still needs tracking when the container carries converted storage.
                if (slice.Pattern != null &&
                    TryAddPatternDesignationAliases(
                        slice.Pattern,
                        input,
                        aliases,
                        semanticModel,
                        root,
                        cancellationToken))
                {
                    added = true;
                }

                continue;
            }

            var elementInput = hasElementSources && elementSources != null
                ? elementSources[i]
                : input;

            if (TryAddPatternDesignationAliases(
                    patterns[i],
                    elementInput,
                    aliases,
                    semanticModel,
                    root,
                    cancellationToken))
            {
                added = true;
            }
        }

        return added;
    }

    /// <summary>
    /// Resolves per-element expressions for a list pattern from a container
    /// creation (literal or symbol initializer). Returns false when the pattern
    /// count does not match the initializer (e.g. slices) so callers can fall
    /// back to the container expression.
    /// </summary>
    private static bool TryGetListPatternElementSources(
        ExpressionSyntax input,
        int patternCount,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken,
        out ExpressionSyntax[]? elements)
    {
        elements = null;
        input = UnwrapParenthesesAndRef(input);

        var elementList = EnumerateContainerElementExpressions(input).ToList();
        if (elementList.Count == 0)
        {
            var symbol = semanticModel.GetSymbolInfo(input, cancellationToken).Symbol;
            if (symbol != null &&
                TryFindSymbolInitializerExpression(symbol, root, semanticModel, out var initializer) &&
                initializer != null)
            {
                elementList = EnumerateContainerElementExpressions(initializer).ToList();
            }
        }

        if (elementList.Count == 0 || elementList.Count != patternCount)
            return false;

        // Slices are counted in patternCount but are not 1:1 with elements.
        elements = elementList.ToArray();
        return true;
    }

    /// <summary>
    /// When an invocation feeds converted storage into a callback lambda via a
    /// method or containing-type type parameter (receiver or argument), tracks
    /// only the lambda parameters whose bound delegate inputs use that type
    /// parameter — e.g. <c>Array.ForEach(new[] { point }, copy =&gt; Bump(ref copy.X))</c>,
    /// <c>points.ForEach(copy =&gt; …)</c> on <c>List&lt;T&gt;</c>, and
    /// <c>other.Zip(points, (n, copy) =&gt; …)</c> (Codex P1), but not an
    /// unrelated sibling lambda such as <c>Run(point, other =&gt; …)</c> whose
    /// delegate input stays the original tuple type (Codex P2).
    /// </summary>
    private static bool TryAddLambdaParameterAliasesFromInvocation(
        InvocationExpressionSyntax invocation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        if (!TryResolveInvocationMethod(invocation, semanticModel, cancellationToken, out var method) ||
            method == null)
        {
            return false;
        }

        var definingMethod = (method.ReducedFrom ?? method).OriginalDefinition;
        var extensionOffset = method.ReducedFrom != null ? 1 : 0;
        var storageTypeParameters = CollectStorageSourcedTypeParametersFromInvocation(
            invocation, method, definingMethod, extensionOffset, aliases, semanticModel, root, cancellationToken);

        if (storageTypeParameters.Count == 0)
            return false;

        var added = false;
        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is not AnonymousFunctionExpressionSyntax)
                continue;

            var reducedParameter = ResolveInvocationParameter(method, argument, i);
            if (reducedParameter == null)
                continue;

            var definingOrdinal = reducedParameter.Ordinal + extensionOffset;
            if (definingOrdinal < 0 || definingOrdinal >= definingMethod.Parameters.Length)
                continue;

            if (TryAddAliasesFromCallbackLambda(
                    expr,
                    definingMethod.Parameters[definingOrdinal].Type,
                    storageTypeParameters,
                    aliases,
                    semanticModel,
                    cancellationToken))
            {
                added = true;
            }
        }

        return added;
    }

    /// <summary>
    /// When converted storage determines a generic <c>out var</c> type, such as
    /// <c>Copy(point, out var copy)</c> with <c>Copy&lt;T&gt;(T value, out T copy)</c>,
    /// tracks the declared <c>out</c> destination as a storage alias (Codex P1).
    /// </summary>
    private static bool TryAddOutVarAliasesFromInvocation(
        InvocationExpressionSyntax invocation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        if (!TryResolveInvocationMethod(invocation, semanticModel, cancellationToken, out var method) ||
            method == null)
        {
            return false;
        }

        var definingMethod = (method.ReducedFrom ?? method).OriginalDefinition;
        var extensionOffset = method.ReducedFrom != null ? 1 : 0;
        var storageTypeParameters = CollectStorageSourcedTypeParametersFromInvocation(
            invocation, method, definingMethod, extensionOffset, aliases, semanticModel, root, cancellationToken);

        if (storageTypeParameters.Count == 0)
            return false;

        var added = false;
        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            if (!argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword))
                continue;

            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is not DeclarationExpressionSyntax { Designation: { } designation })
                continue;

            var reducedParameter = ResolveInvocationParameter(method, argument, i);
            if (reducedParameter == null)
                continue;

            var definingOrdinal = reducedParameter.Ordinal + extensionOffset;
            if (definingOrdinal < 0 || definingOrdinal >= definingMethod.Parameters.Length)
                continue;

            if (!TypeContainsAnyMethodTypeParameter(
                    definingMethod.Parameters[definingOrdinal].Type,
                    storageTypeParameters))
            {
                continue;
            }

            if (TryAddDesignationAlias(designation, aliases, semanticModel, cancellationToken))
                added = true;
        }

        return added;
    }

    /// <summary>
    /// When converted storage is passed to an ordinary method, local function, or
    /// stored delegate (<c>Use(point)</c> / <c>use(point)</c>), tracks the matching
    /// callee parameter so by-ref element uses inside its body are gated — not only
    /// inline lambda callback parameters and <c>out var</c> destinations
    /// (Codex P1 on #3441, PRRT_kwDORD0nYs603sqw).
    /// </summary>
    private static bool TryAddOrdinaryCalleeParameterAliasesFromInvocation(
        InvocationExpressionSyntax invocation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        if (!TryResolveInvocationMethod(invocation, semanticModel, cancellationToken, out var method) ||
            method == null)
        {
            return false;
        }

        var added = false;
        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            // Inline lambdas / out-var declarations are handled by dedicated helpers.
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            ISymbol? parameter = ResolveInvocationParameter(method, argument, i);
            if (parameter == null)
                continue;

            if (method.MethodKind == MethodKind.DelegateInvoke)
            {
                var mapped = TryMapStoredDelegateParameter(
                    invocation, method, i, semanticModel, cancellationToken);
                if (mapped != null)
                    parameter = mapped;
            }
            else if (parameter is IParameterSymbol parameterSymbol)
            {
                // Prefer the unbound generic / source declaration parameter so
                // Use<object>(point) still matches Use<T>(T copy) body references.
                parameter = parameterSymbol.OriginalDefinition;
            }

            if (aliases.Contains(parameter))
                continue;

            aliases.Add(parameter);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// When converted storage is passed to an ordinary constructor
    /// (<c>new Holder(point)</c>), tracks the matching constructor parameter so
    /// by-ref element uses inside its body are gated — including when that body
    /// lives in another partial document (Codex P1 sibling on #3441,
    /// PRRT_kwDORD0nYs604Ela).
    /// </summary>
    private static bool TryAddOrdinaryCalleeParameterAliasesFromObjectCreation(
        BaseObjectCreationExpressionSyntax objectCreation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (objectCreation.ArgumentList == null ||
            objectCreation.ArgumentList.Arguments.Count == 0)
        {
            return false;
        }

        if (semanticModel.GetSymbolInfo(objectCreation, cancellationToken).Symbol is not IMethodSymbol constructor)
            return false;

        var added = false;
        for (var i = 0; i < objectCreation.ArgumentList.Arguments.Count; i++)
        {
            var argument = objectCreation.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            var parameter = ResolveInvocationParameter(constructor, argument, i);
            if (parameter == null)
                continue;

            ISymbol alias = parameter.OriginalDefinition;
            if (aliases.Contains(alias))
                continue;

            aliases.Add(alias);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// When a tracked constructor parameter is forwarded through
    /// <c>: this(value, 0)</c> / <c>: base(value)</c>, tracks the destination
    /// constructor parameter so by-ref element uses inside that body are gated
    /// (Codex P1 on #3441, PRRT_kwDORD0nYs606U0Z).
    /// </summary>
    private static bool TryAddOrdinaryCalleeParameterAliasesFromConstructorInitializer(
        ConstructorInitializerSyntax constructorInitializer,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (constructorInitializer.ArgumentList.Arguments.Count == 0)
            return false;

        if (semanticModel.GetSymbolInfo(constructorInitializer, cancellationToken).Symbol
            is not IMethodSymbol constructor)
        {
            return false;
        }

        var added = false;
        for (var i = 0; i < constructorInitializer.ArgumentList.Arguments.Count; i++)
        {
            var argument = constructorInitializer.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            var parameter = ResolveInvocationParameter(constructor, argument, i);
            if (parameter == null)
                continue;

            ISymbol alias = parameter.OriginalDefinition;
            if (aliases.Contains(alias))
                continue;

            aliases.Add(alias);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// Maps a stored-delegate invocation argument ordinal onto the source lambda /
    /// anonymous-method / method-group parameter the delegate was assigned from
    /// (e.g. <c>Action&lt;dynamic&gt; use = copy =&gt; …; use(point)</c>).
    /// </summary>
    private static ISymbol? TryMapStoredDelegateParameter(
        InvocationExpressionSyntax invocation,
        IMethodSymbol invokeMethod,
        int argumentIndex,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invokeMethod.MethodKind != MethodKind.DelegateInvoke)
            return null;

        var receiver = invocation.Expression is MemberAccessExpressionSyntax
        {
            Name.Identifier.ValueText: "Invoke"
        } invokeAccess
            ? invokeAccess.Expression
            : invocation.Expression;

        receiver = SkipTransparentWrappers(receiver);
        var delegateSymbol = semanticModel.GetSymbolInfo(receiver, cancellationToken).Symbol;
        if (delegateSymbol == null)
            return null;

        // Require exactly one assignment source so a later reassignment
        // (use = _ => {}; use = copy => Bump(...); use(point)) is not mapped to the
        // discarded initializer. Multiple / zero sources are refused via
        // HasUnresolvedCalleeEscape instead (Codex P1 on #3441, PRRT_kwDORD0nYs604ZeH).
        if (!TryGetSingleDelegateAssignmentSource(
                delegateSymbol, invocation, semanticModel, cancellationToken, out var source, out var sourceModel) ||
            source == null ||
            sourceModel == null)
        {
            return null;
        }

        return TryGetDelegateSourceParameter(
            source, argumentIndex, sourceModel, cancellationToken);
    }

    /// <summary>
    /// True when converted storage flows into a call whose parameter mapping cannot
    /// be resolved to a single concrete analyzable target. Conservative refusal
    /// closes reduced extension receivers, virtual/interface/abstract dispatch,
    /// reassigned or uninitialized delegates, expanded <c>params</c> arguments,
    /// and fully unknown/dynamic callees rather than growing precise
    /// interprocedural cases one at a time (Codex P1s on #3441:
    /// PRRT_kwDORD0nYs604ZeC / …604ZeE / …604ZeH / …604ZeL).
    /// </summary>
    private static bool HasUnresolvedCalleeEscape(
        SyntaxNode root,
        SemanticModel semanticModel,
        HashSet<ISymbol> aliases,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    if (InvocationHasUnresolvedCalleeEscape(
                            invocation, aliases, semanticModel, cancellationToken))
                    {
                        return true;
                    }

                    break;

                case BaseObjectCreationExpressionSyntax objectCreation:
                    if (ObjectCreationHasUnresolvedCalleeEscape(
                            objectCreation, aliases, semanticModel, cancellationToken))
                    {
                        return true;
                    }

                    break;

                case ConstructorInitializerSyntax constructorInitializer:
                    // : this(value) / : base(value) with a storage-bearing argument
                    // whose destination ctor cannot be precisely analyzed (Codex P1
                    // on #3441, PRRT_kwDORD0nYs606U0Z).
                    if (ConstructorInitializerHasUnresolvedCalleeEscape(
                            constructorInitializer, aliases, semanticModel, cancellationToken))
                    {
                        return true;
                    }

                    break;
            }

            // Property / indexer setters and user-defined operators / conversions
            // receiving converted storage must bind to one concrete analyzable body
            // (Codex P1s on #3441, PRRT_kwDORD0nYs605hQL / …605hQM).
            if (AccessorHasUnresolvedCalleeEscape(node, aliases, semanticModel, cancellationToken) ||
                OperatorHasUnresolvedCalleeEscape(node, aliases, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InvocationHasUnresolvedCalleeEscape(
        InvocationExpressionSyntax invocation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!TryResolveInvocationMethod(invocation, semanticModel, cancellationToken, out var method) ||
            method == null)
        {
            // Dynamic / unresolved invoke: any storage-bearing receiver or argument escapes.
            return InvocationReceiverOrArgumentReferencesStorage(
                invocation, aliases, semanticModel, cancellationToken);
        }

        // Reduced extension: point.Use() never appears in ArgumentList, so the
        // ordinary argument mapper cannot bind `this`. Refuse when the receiver
        // carries converted storage and is not a sequence/container this (those
        // keep existing ToArray/AsSpan/LINQ precise paths).
        if (method.ReducedFrom is { } reduced &&
            invocation.Expression is MemberAccessExpressionSyntax extensionAccess &&
            ExpressionReferencesStorageAlias(
                extensionAccess.Expression, aliases, semanticModel, cancellationToken) &&
            !ExtensionThisIsSequenceContainer(reduced))
        {
            return true;
        }

        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            if (method.MethodKind == MethodKind.DelegateInvoke)
            {
                if (StoredDelegateArgumentMappingIsUnresolved(
                        invocation, method, i, semanticModel, cancellationToken))
                {
                    return true;
                }

                continue;
            }

            var parameter = ResolveInvocationParameter(method, argument, i);
            if (parameter == null)
            {
                // Expanded params (Use(0, "skip", point) → params dynamic[] copies)
                // or otherwise unmapped argument.
                if (MethodHasParamsParameter(method))
                    return true;

                continue;
            }

            if (parameter.IsParams)
                return true;

            if (!CanPreciselyAnalyzeCalleeBody(method))
                return true;
        }

        return false;
    }

    private static bool ObjectCreationHasUnresolvedCalleeEscape(
        BaseObjectCreationExpressionSyntax objectCreation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (objectCreation.ArgumentList == null ||
            objectCreation.ArgumentList.Arguments.Count == 0)
        {
            return false;
        }

        if (semanticModel.GetSymbolInfo(objectCreation, cancellationToken).Symbol is not IMethodSymbol constructor)
        {
            // Unresolved ctor with a storage-bearing argument escapes.
            return ArgumentListReferencesStorage(
                objectCreation.ArgumentList, aliases, semanticModel, cancellationToken);
        }

        for (var i = 0; i < objectCreation.ArgumentList.Arguments.Count; i++)
        {
            var argument = objectCreation.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            var parameter = ResolveInvocationParameter(constructor, argument, i);
            if (parameter == null)
            {
                if (MethodHasParamsParameter(constructor))
                    return true;

                continue;
            }

            if (parameter.IsParams)
                return true;

            if (!CanPreciselyAnalyzeCalleeBody(constructor))
                return true;
        }

        return false;
    }

    private static bool ConstructorInitializerHasUnresolvedCalleeEscape(
        ConstructorInitializerSyntax constructorInitializer,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (constructorInitializer.ArgumentList.Arguments.Count == 0)
            return false;

        if (semanticModel.GetSymbolInfo(constructorInitializer, cancellationToken).Symbol
            is not IMethodSymbol constructor)
        {
            // Unresolved this()/base() target with a storage-bearing argument escapes.
            return ArgumentListReferencesStorage(
                constructorInitializer.ArgumentList, aliases, semanticModel, cancellationToken);
        }

        for (var i = 0; i < constructorInitializer.ArgumentList.Arguments.Count; i++)
        {
            var argument = constructorInitializer.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            var parameter = ResolveInvocationParameter(constructor, argument, i);
            if (parameter == null)
            {
                if (MethodHasParamsParameter(constructor))
                    return true;

                continue;
            }

            if (parameter.IsParams)
                return true;

            if (!CanPreciselyAnalyzeCalleeBody(constructor))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Accessor parameters that receive converted storage: the setter / init
    /// <c>value</c> parameter for <c>holder.Value = point</c> / <c>holder[0] = point</c>
    /// / <c>new Holder { Value = point }</c>, and indexer index parameters for
    /// <c>holder[point]</c> (both accessors). Bodies are then gated like ordinary
    /// callee parameters (Codex P1 on #3441, PRRT_kwDORD0nYs605hQL).
    /// </summary>
    private static bool TryAddAccessorParameterAliases(
        SyntaxNode node,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var added = false;

        if (node is AssignmentExpressionSyntax assignment &&
            (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
             assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)) &&
            TryGetAssignedPropertySetter(assignment, semanticModel, cancellationToken, out var setter) &&
            !setter.Parameters.IsDefaultOrEmpty &&
            ExpressionReferencesStorageAlias(assignment.Right, aliases, semanticModel, cancellationToken))
        {
            added |= aliases.Add(setter.OriginalDefinition.Parameters[^1]);
        }

        if (node is ElementAccessExpressionSyntax elementAccess &&
            semanticModel.GetSymbolInfo(elementAccess, cancellationToken).Symbol is IPropertySymbol { IsIndexer: true } indexer)
        {
            var arguments = elementAccess.ArgumentList.Arguments;
            for (var i = 0; i < arguments.Count; i++)
            {
                if (!ExpressionReferencesStorageAlias(
                        arguments[i].Expression, aliases, semanticModel, cancellationToken))
                {
                    continue;
                }

                var parameter = ResolveIndexerParameter(indexer, arguments[i], i);
                if (parameter == null)
                    continue;

                foreach (var accessor in new[] { indexer.GetMethod, indexer.SetMethod })
                {
                    if (accessor == null || parameter.Ordinal >= accessor.Parameters.Length)
                        continue;
                    added |= aliases.Add(accessor.OriginalDefinition.Parameters[parameter.Ordinal]);
                }
            }
        }

        return added;
    }

    private static IParameterSymbol? ResolveIndexerParameter(
        IPropertySymbol indexer,
        ArgumentSyntax argument,
        int index)
    {
        if (argument.NameColon is { } nameColon)
        {
            return indexer.Parameters.FirstOrDefault(p =>
                p.Name == nameColon.Name.Identifier.ValueText);
        }

        if (index < indexer.Parameters.Length)
            return indexer.Parameters[index];

        return null;
    }

    private static bool TryGetAssignedPropertySetter(
        AssignmentExpressionSyntax assignment,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out IMethodSymbol? setter)
    {
        setter = null;
        var left = UnwrapParenthesesAndNullForgiving(assignment.Left);
        if (semanticModel.GetSymbolInfo(left, cancellationToken).Symbol is not IPropertySymbol property)
            return false;

        setter = property.SetMethod;
        return setter != null;
    }

    /// <summary>
    /// <c>consumer + point</c> / <c>-point</c> / <c>acc += point</c> bound to a
    /// user-defined operator, and <c>(Wrapper)point</c> / <c>Wrapper w = point</c>
    /// bound to a user-defined conversion: maps each storage-bearing operand to the
    /// operator / conversion parameter so its body is gated like an ordinary callee
    /// (Codex P1 on #3441, PRRT_kwDORD0nYs605hQM).
    /// </summary>
    private static bool TryAddUserDefinedOperatorParameterAliases(
        SyntaxNode node,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var added = false;
        foreach (var (operand, parameter) in EnumerateUserDefinedOperatorOperands(
                     node, semanticModel, cancellationToken))
        {
            if (parameter == null ||
                !ExpressionReferencesStorageAlias(operand, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            added |= aliases.Add(parameter.OriginalDefinition);
        }

        return added;
    }

    /// <summary>
    /// Yields (operand, bound parameter) pairs for a node that invokes a
    /// user-defined operator or conversion. The parameter is null when the
    /// operand cannot be mapped (dynamic binding is handled by
    /// <see cref="OperatorHasUnresolvedCalleeEscape"/>).
    /// </summary>
    private static IEnumerable<(ExpressionSyntax Operand, IParameterSymbol? Parameter)> EnumerateUserDefinedOperatorOperands(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        switch (node)
        {
            case BinaryExpressionSyntax binary
                when !binary.IsKind(SyntaxKind.AsExpression) &&
                     !binary.IsKind(SyntaxKind.IsExpression) &&
                     !binary.IsKind(SyntaxKind.CoalesceExpression):
                if (TryGetUserDefinedOperator(binary, semanticModel, cancellationToken) is { } binaryOperator)
                {
                    yield return (binary.Left, ParameterAt(binaryOperator, 0));
                    yield return (binary.Right, ParameterAt(binaryOperator, 1));
                }

                break;

            case PrefixUnaryExpressionSyntax prefix:
                if (TryGetUserDefinedOperator(prefix, semanticModel, cancellationToken) is { } prefixOperator)
                    yield return (prefix.Operand, ParameterAt(prefixOperator, 0));
                break;

            case PostfixUnaryExpressionSyntax postfix
                when !postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                if (TryGetUserDefinedOperator(postfix, semanticModel, cancellationToken) is { } postfixOperator)
                    yield return (postfix.Operand, ParameterAt(postfixOperator, 0));
                break;

            case AssignmentExpressionSyntax compound
                when !compound.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                     !compound.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                if (TryGetUserDefinedOperator(compound, semanticModel, cancellationToken) is { } compoundOperator)
                {
                    if (compoundOperator.Parameters.Length == 1 && !compoundOperator.IsStatic)
                    {
                        // C# 14 instance compound-assignment operator: right → param 0.
                        yield return (compound.Right, ParameterAt(compoundOperator, 0));
                    }
                    else
                    {
                        yield return (compound.Left, ParameterAt(compoundOperator, 0));
                        yield return (compound.Right, ParameterAt(compoundOperator, 1));
                    }
                }

                break;
        }

        // User-defined conversions: explicit casts, and implicit conversions
        // applied to an expression in its converted context.
        if (node is CastExpressionSyntax cast)
        {
            var castOperator = semanticModel.GetOperation(cast, cancellationToken) is
                Microsoft.CodeAnalysis.Operations.IConversionOperation { OperatorMethod: { } conversionMethod }
                ? conversionMethod
                : semanticModel.GetConversion(cast, cancellationToken) is { IsUserDefined: true, MethodSymbol: { } fallback }
                    ? fallback
                    : null;
            if (castOperator != null)
                yield return (cast.Expression, ParameterAt(castOperator, 0));
        }
        else if (node is ExpressionSyntax expression &&
                 node is not (AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax) &&
                 !(node.Parent is ParenthesizedExpressionSyntax) &&
                 semanticModel.GetConversion(expression, cancellationToken) is { IsUserDefined: true, MethodSymbol: { } implicitOperator })
        {
            yield return (expression, ParameterAt(implicitOperator, 0));
        }
    }

    private static IParameterSymbol? ParameterAt(IMethodSymbol method, int ordinal) =>
        ordinal < method.Parameters.Length ? method.Parameters[ordinal] : null;

    private static IMethodSymbol? TryGetUserDefinedOperator(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol is IMethodSymbol
        {
            MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion
        } method
            ? method
            : null;

    /// <summary>
    /// A property / indexer assignment of converted storage whose setter is
    /// abstract, interface-declared, or open-virtual cannot be attributed to one
    /// setter body (Codex P1 on #3441, PRRT_kwDORD0nYs605hQL).
    /// </summary>
    private static bool AccessorHasUnresolvedCalleeEscape(
        SyntaxNode node,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (node is not AssignmentExpressionSyntax assignment ||
            !(assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
              assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)))
        {
            return false;
        }

        if (!TryGetAssignedPropertySetter(assignment, semanticModel, cancellationToken, out var setter))
        {
            // Dynamic member / indexer assignment (dynamicHolder.Value = point):
            // the runtime binder may pick any setter.
            var left = UnwrapParenthesesAndNullForgiving(assignment.Left);
            return left is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax &&
                   semanticModel.GetSymbolInfo(left, cancellationToken).Symbol == null &&
                   semanticModel.GetTypeInfo(left, cancellationToken).Type is { TypeKind: TypeKind.Dynamic } &&
                   ExpressionReferencesStorageAlias(assignment.Right, aliases, semanticModel, cancellationToken);
        }

        return !CanPreciselyAnalyzeCalleeBody(setter) &&
               ExpressionReferencesStorageAlias(assignment.Right, aliases, semanticModel, cancellationToken);
    }

    /// <summary>
    /// Storage-bearing operands of an operator whose target cannot be resolved to
    /// one concrete body: static abstract / virtual interface operators, and
    /// dynamically bound operators whose other operand's source type declares
    /// user-defined operators / conversions (Codex P1 on #3441,
    /// PRRT_kwDORD0nYs605hQM).
    /// </summary>
    private static bool OperatorHasUnresolvedCalleeEscape(
        SyntaxNode node,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var (operand, parameter) in EnumerateUserDefinedOperatorOperands(
                     node, semanticModel, cancellationToken))
        {
            if (!ExpressionReferencesStorageAlias(operand, aliases, semanticModel, cancellationToken))
                continue;

            if (parameter == null || !CanPreciselyAnalyzeCalleeBody((IMethodSymbol)parameter.ContainingSymbol))
                return true;
        }

        // Dynamic binding: consumer + copy where copy is dynamic picks
        // operator +(Consumer, object) at runtime.
        ExpressionSyntax[] operands = node switch
        {
            BinaryExpressionSyntax binary
                when !binary.IsKind(SyntaxKind.AsExpression) &&
                     !binary.IsKind(SyntaxKind.IsExpression) &&
                     !binary.IsKind(SyntaxKind.CoalesceExpression) &&
                     !binary.IsKind(SyntaxKind.LogicalAndExpression) &&
                     !binary.IsKind(SyntaxKind.LogicalOrExpression) => new[] { binary.Left, binary.Right },
            AssignmentExpressionSyntax compound
                when !compound.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                     !compound.IsKind(SyntaxKind.CoalesceAssignmentExpression) => new[] { compound.Left, compound.Right },
            _ => Array.Empty<ExpressionSyntax>()
        };

        if (operands.Length != 2)
            return false;

        // Roslyn reports dynamic binding as a synthesized built-in
        // `dynamic.operator +` with CandidateReason.LateBound.
        var operatorInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
        var lateBound = operatorInfo.Symbol == null ||
                        operatorInfo.CandidateReason == CandidateReason.LateBound ||
                        operands.Any(o => semanticModel.GetTypeInfo(o, cancellationToken).Type is { TypeKind: TypeKind.Dynamic });
        if (!lateBound)
            return false;

        for (var i = 0; i < 2; i++)
        {
            if (!ExpressionReferencesStorageAlias(operands[i], aliases, semanticModel, cancellationToken))
                continue;

            var other = semanticModel.GetTypeInfo(operands[1 - i], cancellationToken).Type;
            if (other is INamedTypeSymbol { DeclaringSyntaxReferences.IsDefaultOrEmpty: false } named &&
                TypeDeclaresUserDefinedOperators(named))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TypeDeclaresUserDefinedOperators(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? cursor = type; cursor != null; cursor = cursor.BaseType)
        {
            if (cursor.GetMembers().OfType<IMethodSymbol>().Any(m =>
                    m.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InvocationReceiverOrArgumentReferencesStorage(
        InvocationExpressionSyntax invocation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            ExpressionReferencesStorageAlias(
                memberAccess.Expression, aliases, semanticModel, cancellationToken))
        {
            return true;
        }

        return ArgumentListReferencesStorage(
            invocation.ArgumentList, aliases, semanticModel, cancellationToken);
    }

    private static bool ArgumentListReferencesStorage(
        ArgumentListSyntax argumentList,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var argument in argumentList.Arguments)
        {
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            if (ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a reduced extension's <c>this</c> parameter is a sequence/container
    /// type already handled by collection-forwarding precise paths
    /// (<c>points.ToArray()</c> / LINQ). Scalar <c>this object</c> /
    /// <c>this dynamic</c> / tuple / type-parameter receivers are not.
    /// </summary>
    private static bool ExtensionThisIsSequenceContainer(IMethodSymbol unreducedExtension)
    {
        if (unreducedExtension.Parameters.IsDefaultOrEmpty)
            return false;

        return TryGetSequenceElementType(unreducedExtension.Parameters[0].Type) != null;
    }

    private static bool MethodHasParamsParameter(IMethodSymbol method) =>
        !method.Parameters.IsDefaultOrEmpty && method.Parameters[^1].IsParams;

    /// <summary>
    /// True when the statically bound <paramref name="method"/> is a single concrete
    /// target we can attribute this call to. Abstract/interface/open-virtual dispatch
    /// maps to the wrong parameter symbol for implementation bodies, so those escape.
    /// Metadata-only ordinary methods still resolve a parameter mapping (existing
    /// precise alias path); only uncertain dispatch is refused here.
    /// </summary>
    private static bool CanPreciselyAnalyzeCalleeBody(IMethodSymbol method)
    {
        var original = (method.ReducedFrom ?? method).OriginalDefinition;

        if (original.MethodKind == MethodKind.DelegateInvoke)
            return false;

        if (original.IsAbstract)
            return false;

        if (original.ContainingType?.TypeKind == TypeKind.Interface)
            return false;

        // Open virtual dispatch — overrides may differ from the static target.
        if (original.IsVirtual && !original.IsSealed)
            return false;

        return true;
    }

    private static bool StoredDelegateArgumentMappingIsUnresolved(
        InvocationExpressionSyntax invocation,
        IMethodSymbol invokeMethod,
        int argumentIndex,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var receiver = invocation.Expression is MemberAccessExpressionSyntax
        {
            Name.Identifier.ValueText: "Invoke"
        } invokeAccess
            ? invokeAccess.Expression
            : invocation.Expression;

        receiver = SkipTransparentWrappers(receiver);
        var delegateSymbol = semanticModel.GetSymbolInfo(receiver, cancellationToken).Symbol;
        if (delegateSymbol == null)
            return true;

        if (!TryGetSingleDelegateAssignmentSource(
                delegateSymbol, invocation, semanticModel, cancellationToken, out var source, out var sourceModel) ||
            source == null ||
            sourceModel == null)
        {
            return true;
        }

        return TryGetDelegateSourceParameter(
            source, argumentIndex, sourceModel, cancellationToken) == null;
    }

    /// <summary>
    /// Returns the sole initializer/assignment expression that defines
    /// <paramref name="delegateSymbol"/> across its declaring documents, or
    /// <c>false</c> when there are zero or multiple sources (reassignment).
    /// </summary>
    private static bool TryGetSingleDelegateAssignmentSource(
        ISymbol delegateSymbol,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ExpressionSyntax? source,
        out SemanticModel? sourceModel)
    {
        source = null;
        sourceModel = null;

        ExpressionSyntax? found = null;
        SemanticModel? foundModel = null;
        var root = invocation.SyntaxTree.GetRoot(cancellationToken);

        foreach (var candidate in EnumerateDelegateAssignmentSources(
                     delegateSymbol, root, semanticModel, cancellationToken))
        {
            if (found != null)
                return false;

            found = candidate;
            foundModel = semanticModel;
        }

        foreach (var (otherRoot, otherModel) in EnumerateDelegateSymbolDocumentRoots(
                     delegateSymbol, semanticModel, cancellationToken))
        {
            if (otherRoot.SyntaxTree == root.SyntaxTree)
                continue;

            foreach (var candidate in EnumerateDelegateAssignmentSources(
                         delegateSymbol, otherRoot, otherModel, cancellationToken))
            {
                if (found != null)
                    return false;

                found = candidate;
                foundModel = otherModel;
            }
        }

        if (found == null || foundModel == null)
            return false;

        source = found;
        sourceModel = foundModel;
        return true;
    }

    /// <summary>
    /// Yields roots+models for documents that declare <paramref name="delegateSymbol"/>
    /// or (for fields/properties) other partial parts of its containing type, so
    /// stored-delegate sources assigned in sibling files are still mapped.
    /// </summary>
    private static IEnumerable<(SyntaxNode Root, SemanticModel Model)> EnumerateDelegateSymbolDocumentRoots(
        ISymbol delegateSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<SyntaxTree>();
        foreach (var reference in EnumerateDelegateRelatedSyntaxReferences(delegateSymbol))
        {
            var tree = reference.SyntaxTree;
            if (!seen.Add(tree))
                continue;

            var root = tree.GetRoot(cancellationToken);
            var model = tree == semanticModel.SyntaxTree
                ? semanticModel
                : semanticModel.Compilation.GetSemanticModel(tree);
            yield return (root, model);
        }
    }

    private static IEnumerable<SyntaxReference> EnumerateDelegateRelatedSyntaxReferences(ISymbol delegateSymbol)
    {
        foreach (var reference in delegateSymbol.DeclaringSyntaxReferences)
            yield return reference;

        if (delegateSymbol is not (IFieldSymbol or IPropertySymbol) ||
            delegateSymbol.ContainingType == null)
        {
            yield break;
        }

        foreach (var reference in delegateSymbol.ContainingType.DeclaringSyntaxReferences)
            yield return reference;
    }

    private static IEnumerable<ExpressionSyntax> EnumerateDelegateAssignmentSources(
        ISymbol delegateSymbol,
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is VariableDeclaratorSyntax
                {
                    Initializer.Value: { } value
                } declarator)
            {
                var declared = semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
                if (declared != null &&
                    SymbolEqualityComparer.Default.Equals(declared, delegateSymbol))
                {
                    yield return value;
                }
            }
            else if (node is AssignmentExpressionSyntax assignment &&
                     assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                var left = semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
                if (left != null &&
                    SymbolEqualityComparer.Default.Equals(left, delegateSymbol))
                {
                    yield return assignment.Right;
                }
            }
        }
    }

    private static ISymbol? TryGetDelegateSourceParameter(
        ExpressionSyntax source,
        int argumentIndex,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        source = SkipTransparentWrappers(source);
        while (TryUnwrapValuePreservingCast(
                   source, semanticModel, cancellationToken, out var castOperand))
        {
            source = SkipTransparentWrappers(castOperand);
        }

        IReadOnlyList<ParameterSyntax>? parameters = source switch
        {
            SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter },
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters,
            AnonymousMethodExpressionSyntax { ParameterList: { } list } => list.Parameters,
            _ => null
        };

        if (parameters != null)
        {
            if (argumentIndex < 0 || argumentIndex >= parameters.Count)
                return null;
            return semanticModel.GetDeclaredSymbol(parameters[argumentIndex], cancellationToken);
        }

        // Method group: Action<dynamic> use = Use;
        var group = semanticModel.GetSymbolInfo(source, cancellationToken).Symbol as IMethodSymbol;
        if (group == null)
        {
            var info = semanticModel.GetSymbolInfo(source, cancellationToken);
            if (info.CandidateSymbols.Length == 1)
                group = info.CandidateSymbols[0] as IMethodSymbol;
        }

        if (group == null)
            return null;

        var original = (group.ReducedFrom ?? group).OriginalDefinition;
        var extensionOffset = group.ReducedFrom != null ? 1 : 0;
        var ordinal = argumentIndex + extensionOffset;
        if (ordinal < 0 || ordinal >= original.Parameters.Length)
            return null;

        return original.Parameters[ordinal];
    }

    private static bool TryResolveInvocationMethod(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out IMethodSymbol? method)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
        method = symbolInfo.Symbol as IMethodSymbol;
        if (method == null &&
            symbolInfo.CandidateSymbols.Length == 1)
        {
            method = symbolInfo.CandidateSymbols[0] as IMethodSymbol;
        }

        return method != null;
    }

    /// <summary>
    /// Collects method / containing-type type parameters sourced by converted
    /// storage on an invocation: storage-carrying arguments, reduced extension
    /// receivers, and constructed generic instance receivers such as
    /// <c>List&lt;T&gt;.ForEach</c> (Codex P1).
    /// </summary>
    private static HashSet<ITypeParameterSymbol> CollectStorageSourcedTypeParametersFromInvocation(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        IMethodSymbol definingMethod,
        int extensionOffset,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken)
    {
        var storageTypeParameters = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);

        if (extensionOffset == 1 &&
            invocation.Expression is MemberAccessExpressionSyntax extensionReceiver &&
            definingMethod.Parameters.Length > 0 &&
            ExpressionReferencesStorageAlias(
                extensionReceiver.Expression, aliases, semanticModel, cancellationToken))
        {
            CollectMethodTypeParameters(definingMethod.Parameters[0].Type, storageTypeParameters);
        }

        // Instance method on a constructed generic whose receiver carries storage
        // (e.g. List&lt;T&gt;.ForEach) — map containing-type parameters (Codex P1).
        if (method.ReducedFrom == null &&
            invocation.Expression is MemberAccessExpressionSyntax instanceReceiver &&
            method.ContainingType is { IsGenericType: true } containingType &&
            ExpressionReferencesStorageAlias(
                instanceReceiver.Expression, aliases, semanticModel, cancellationToken))
        {
            CollectStorageSourcedContainingTypeParameters(
                containingType,
                instanceReceiver.Expression,
                aliases,
                semanticModel,
                root,
                storageTypeParameters,
                cancellationToken);
        }

        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            var expr = SkipTransparentWrappers(argument.Expression);
            if (expr is AnonymousFunctionExpressionSyntax or DeclarationExpressionSyntax)
                continue;

            var reducedParameter = ResolveInvocationParameter(method, argument, i);
            if (reducedParameter == null)
                continue;

            var definingOrdinal = reducedParameter.Ordinal + extensionOffset;
            if (definingOrdinal < 0 || definingOrdinal >= definingMethod.Parameters.Length)
                continue;

            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            CollectMethodTypeParameters(
                definingMethod.Parameters[definingOrdinal].Type,
                storageTypeParameters);
        }

        return storageTypeParameters;
    }

    /// <summary>
    /// Adds containing-type type parameters whose constructed type arguments
    /// involve a tracked storage alias type (e.g. <c>T</c> from
    /// <c>List&lt;(int X, int Y)&gt;</c> when <c>point</c>/<c>points</c> is tracked).
    /// Prefers factory provenance so <c>Runner&lt;T, (int X, int Y)&gt; Make&lt;T&gt;(T)</c>
    /// only marks <c>T</c>, not the unrelated second type argument (Codex P2 on #3441).
    /// </summary>
    private static void CollectStorageSourcedContainingTypeParameters(
        INamedTypeSymbol constructedContainingType,
        ExpressionSyntax receiverExpression,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        HashSet<ITypeParameterSymbol> into,
        CancellationToken cancellationToken)
    {
        if (TryCollectStorageSourcedContainingTypeParametersFromProvenance(
                receiverExpression,
                constructedContainingType,
                aliases,
                semanticModel,
                root,
                into,
                cancellationToken))
        {
            return;
        }

        var original = constructedContainingType.OriginalDefinition;
        var typeArguments = constructedContainingType.TypeArguments;
        var limit = Math.Min(typeArguments.Length, original.TypeParameters.Length);
        for (var i = 0; i < limit; i++)
        {
            if (TypeArgumentEmbedsStorageAliasType(typeArguments[i], aliases))
                into.Add(original.TypeParameters[i]);
        }
    }

    /// <summary>
    /// True when <paramref name="typeArgument"/> equals or embeds an element-shaped
    /// storage alias type. Skips composite carrier aliases (<c>List&lt;T&gt;</c>,
    /// <c>Runner&lt;T,U&gt;</c>) so reverse containment cannot mark sibling type
    /// parameters (Codex P2 on #3441).
    /// </summary>
    private static bool TypeArgumentEmbedsStorageAliasType(ITypeSymbol typeArgument, HashSet<ISymbol> aliases)
    {
        foreach (var alias in aliases)
        {
            var aliasType = GetAliasType(alias);
            if (aliasType == null)
                continue;

            // Composite carriers: matching via TypeContainsOrEquals(carrier, typeArg)
            // would mark every type argument of Runner&lt;T,U&gt;. Element aliases
            // (the original tuple / direct copies) still match.
            if (aliasType is INamedTypeSymbol { IsGenericType: true, IsTupleType: false })
                continue;

            if (SymbolEqualityComparer.Default.Equals(typeArgument, aliasType) ||
                TypeContainsOrEquals(typeArgument, aliasType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When a tracked receiver was produced by a generic factory that only embeds
    /// storage in some return-type arguments (e.g. <c>Runner&lt;T, (int X, int Y)&gt;
    /// Make&lt;T&gt;(T value)</c>), collects only those containing-type type parameters
    /// derived from the storage-bearing factory parameter (Codex P2 on #3441).
    /// </summary>
    private static bool TryCollectStorageSourcedContainingTypeParametersFromProvenance(
        ExpressionSyntax receiverExpression,
        INamedTypeSymbol constructedContainingType,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        HashSet<ITypeParameterSymbol> into,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var current = UnwrapParenthesesAndNullForgiving(receiverExpression);

        while (true)
        {
            if (TryCollectContainingTypeParametersFromFactoryInvocation(
                    current,
                    constructedContainingType,
                    aliases,
                    semanticModel,
                    into,
                    cancellationToken))
            {
                return true;
            }

            var symbol = semanticModel.GetSymbolInfo(current, cancellationToken).Symbol;
            if (symbol == null || !visited.Add(symbol))
                return false;

            if (!TryFindSymbolInitializerExpression(symbol, root, semanticModel, out var initializer) ||
                initializer == null)
            {
                return false;
            }

            current = UnwrapParenthesesAndNullForgiving(initializer);
        }
    }

    /// <summary>
    /// Maps storage-sourced method type parameters through a factory return type
    /// onto the constructed containing type's type parameters.
    /// </summary>
    private static bool TryCollectContainingTypeParametersFromFactoryInvocation(
        ExpressionSyntax expression,
        INamedTypeSymbol constructedContainingType,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        HashSet<ITypeParameterSymbol> into,
        CancellationToken cancellationToken)
    {
        expression = UnwrapParenthesesAndNullForgiving(expression);
        if (expression is not InvocationExpressionSyntax invocation)
            return false;

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            return false;

        var definingMethod = (method.ReducedFrom ?? method).OriginalDefinition;
        if (definingMethod.ReturnType is not INamedTypeSymbol originalReturn)
            return false;

        if (!SymbolEqualityComparer.Default.Equals(
                originalReturn.OriginalDefinition,
                constructedContainingType.OriginalDefinition))
        {
            return false;
        }

        var storageMethodTypeParameters = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        var extensionOffset = method.ReducedFrom != null ? 1 : 0;

        if (extensionOffset == 1 &&
            invocation.Expression is MemberAccessExpressionSyntax extensionReceiver &&
            definingMethod.Parameters.Length > 0 &&
            ExpressionReferencesStorageAlias(
                extensionReceiver.Expression, aliases, semanticModel, cancellationToken))
        {
            CollectMethodTypeParameters(definingMethod.Parameters[0].Type, storageMethodTypeParameters);
        }

        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            var reducedParameter = ResolveInvocationParameter(method, argument, i);
            if (reducedParameter == null)
                continue;

            var definingOrdinal = reducedParameter.Ordinal + extensionOffset;
            if (definingOrdinal < 0 || definingOrdinal >= definingMethod.Parameters.Length)
                continue;

            CollectMethodTypeParameters(
                definingMethod.Parameters[definingOrdinal].Type,
                storageMethodTypeParameters);
        }

        if (storageMethodTypeParameters.Count == 0)
            return false;

        var constructedOriginal = constructedContainingType.OriginalDefinition;
        var limit = Math.Min(originalReturn.TypeArguments.Length, constructedOriginal.TypeParameters.Length);
        var added = false;
        for (var i = 0; i < limit; i++)
        {
            if (!TypeContainsAnyMethodTypeParameter(
                    originalReturn.TypeArguments[i], storageMethodTypeParameters))
            {
                continue;
            }

            into.Add(constructedOriginal.TypeParameters[i]);
            added = true;
        }

        return added;
    }

    private static bool TypeInvolvesStorageAliasType(ITypeSymbol type, HashSet<ISymbol> aliases)
    {
        foreach (var alias in aliases)
        {
            var aliasType = GetAliasType(alias);
            if (aliasType == null)
                continue;

            if (SymbolEqualityComparer.Default.Equals(type, aliasType) ||
                TypeContainsOrEquals(aliasType, type) ||
                TypeContainsOrEquals(type, aliasType))
            {
                return true;
            }
        }

        return false;
    }

    private static ITypeSymbol? GetAliasType(ISymbol alias) => alias switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        IEventSymbol eventSymbol => eventSymbol.Type,
        _ => null
    };

    private static bool TypeContainsOrEquals(ITypeSymbol type, ITypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(type, target))
            return true;

        switch (type)
        {
            case IArrayTypeSymbol array:
                return TypeContainsOrEquals(array.ElementType, target);
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments)
                {
                    if (TypeContainsOrEquals(argument, target))
                        return true;
                }

                if (named.IsTupleType)
                {
                    foreach (var element in named.TupleElements)
                    {
                        if (TypeContainsOrEquals(element.Type, target))
                            return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Adds foreach-deconstruction designations whose declared type involves
    /// tracked storage (<c>foreach (var (copy, _) in …)</c>) when the enumerated
    /// expression already carries converted storage (Codex P1).
    /// </summary>
    private static bool TryAddForeachVariableDesignationAliases(
        ExpressionSyntax variable,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        variable = UnwrapParenthesesAndNullForgiving(variable);

        if (variable is DeclarationExpressionSyntax { Designation: { } designation })
        {
            return TryAddStorageInvolvingDesignationAliases(
                designation, aliases, semanticModel, cancellationToken);
        }

        // foreach ((var copy, var _) in …) — tuple of declaration expressions
        if (variable is TupleExpressionSyntax tuple)
        {
            var added = false;
            foreach (var argument in tuple.Arguments)
            {
                if (TryAddForeachVariableDesignationAliases(
                        argument.Expression, aliases, semanticModel, cancellationToken))
                {
                    added = true;
                }
            }

            return added;
        }

        return false;
    }

    /// <summary>
    /// Walks a designation tree and records locals whose type involves tracked
    /// storage (skips discards and unrelated <c>int</c> partners).
    /// </summary>
    private static bool TryAddStorageInvolvingDesignationAliases(
        VariableDesignationSyntax designation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        switch (designation)
        {
            case SingleVariableDesignationSyntax single:
                {
                    var alias = semanticModel.GetDeclaredSymbol(single, cancellationToken);
                    if (alias == null || aliases.Contains(alias))
                        return false;

                    var type = GetAliasType(alias);
                    if (type == null || !TypeInvolvesStorageAliasType(type, aliases))
                        return false;

                    aliases.Add(alias);
                    return true;
                }
            case ParenthesizedVariableDesignationSyntax parenthesized:
                {
                    var added = false;
                    foreach (var nested in parenthesized.Variables)
                    {
                        if (TryAddStorageInvolvingDesignationAliases(
                                nested, aliases, semanticModel, cancellationToken))
                        {
                            added = true;
                        }
                    }

                    return added;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// True when a query's final <c>select</c>/<c>group</c> projection (after
    /// any <c>into</c> continuations) yields converted storage.
    /// </summary>
    private static bool QueryProjectionYieldsConvertedStorage(
        QueryExpressionSyntax queryExpression,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var body = queryExpression.Body;
        while (body.Continuation != null)
            body = body.Continuation.Body;

        return body.SelectOrGroup switch
        {
            SelectClauseSyntax select => ExpressionReferencesStorageAlias(
                select.Expression, aliases, semanticModel, cancellationToken),
            GroupClauseSyntax group => ExpressionReferencesStorageAlias(
                group.GroupExpression, aliases, semanticModel, cancellationToken),
            _ => false
        };
    }

    private static bool TryAddDesignationAlias(
        VariableDesignationSyntax designation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        switch (designation)
        {
            case SingleVariableDesignationSyntax single:
                {
                    var alias = semanticModel.GetDeclaredSymbol(single, cancellationToken);
                    if (alias == null || aliases.Contains(alias))
                        return false;

                    aliases.Add(alias);
                    return true;
                }
            case ParenthesizedVariableDesignationSyntax parenthesized:
                {
                    var added = false;
                    foreach (var nested in parenthesized.Variables)
                    {
                        if (TryAddDesignationAlias(nested, aliases, semanticModel, cancellationToken))
                            added = true;
                    }

                    return added;
                }
            default:
                return false;
        }
    }

    private static bool TryAddAliasesFromCallbackLambda(
        ExpressionSyntax expression,
        ITypeSymbol delegateParameterType,
        HashSet<ITypeParameterSymbol> storageTypeParameters,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (delegateParameterType is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
            return false;

        IReadOnlyList<ParameterSyntax>? parameters = expression switch
        {
            SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter },
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters,
            AnonymousMethodExpressionSyntax { ParameterList: { } list } => list.Parameters,
            _ => null
        };

        if (parameters == null || parameters.Count == 0)
            return false;

        var added = false;
        var limit = Math.Min(parameters.Count, invoke.Parameters.Length);
        for (var i = 0; i < limit; i++)
        {
            if (!TypeContainsAnyMethodTypeParameter(invoke.Parameters[i].Type, storageTypeParameters))
                continue;

            var alias = semanticModel.GetDeclaredSymbol(parameters[i], cancellationToken);
            if (alias == null || aliases.Contains(alias))
                continue;

            aliases.Add(alias);
            added = true;
        }

        return added;
    }

    /// <summary>
    /// Collects method type parameters appearing in <paramref name="type"/>
    /// (e.g. <c>T</c> from <c>IEnumerable&lt;T&gt;</c> / <c>T[]</c> / <c>Action&lt;T&gt;</c>).
    /// </summary>
    private static void CollectMethodTypeParameters(
        ITypeSymbol type,
        HashSet<ITypeParameterSymbol> into)
    {
        switch (type)
        {
            case ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } typeParameter:
                into.Add(typeParameter);
                break;
            case IArrayTypeSymbol array:
                CollectMethodTypeParameters(array.ElementType, into);
                break;
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments)
                    CollectMethodTypeParameters(argument, into);
                break;
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> mentions any of
    /// <paramref name="typeParameters"/>.
    /// </summary>
    private static bool TypeContainsAnyMethodTypeParameter(
        ITypeSymbol type,
        HashSet<ITypeParameterSymbol> typeParameters)
    {
        switch (type)
        {
            case ITypeParameterSymbol typeParameter:
                return typeParameters.Contains(typeParameter);
            case IArrayTypeSymbol array:
                return TypeContainsAnyMethodTypeParameter(array.ElementType, typeParameters);
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments)
                {
                    if (TypeContainsAnyMethodTypeParameter(argument, typeParameters))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves the expression that a recursive property subpattern binds against
    /// (e.g. <c>Value</c> in <c>new { Value = point } is { Value: var copy }</c>).
    /// </summary>
    private static bool TryResolvePatternMemberInput(
        ExpressionSyntax input,
        string memberName,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken,
        out ExpressionSyntax? memberInput)
    {
        memberInput = null;
        input = UnwrapParenthesesAndRef(input);

        if (TryGetNamedMemberInitializer(input, memberName, semanticModel, out memberInput) &&
            memberInput != null)
        {
            return true;
        }

        var symbol = semanticModel.GetSymbolInfo(input, cancellationToken).Symbol;
        if (symbol != null &&
            TryFindSymbolInitializerExpression(symbol, root, semanticModel, out var initializer) &&
            initializer != null)
        {
            return TryResolvePatternMemberInput(
                initializer,
                memberName,
                aliases,
                semanticModel,
                root,
                cancellationToken,
                out memberInput);
        }

        foreach (var creation in EnumerateAggregateCreations(input, aliases, semanticModel, root))
        {
            if (TryGetNamedMemberInitializer(creation, memberName, semanticModel, out memberInput) &&
                memberInput != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a (possibly chained) member read such as <c>box.Inner.Value</c>
    /// or <c>(point, 0).Item1</c> to the aggregate initializer expression it
    /// reads, following anonymous-object / tuple creations and local initializers.
    /// </summary>
    private static bool TryResolveAggregateMemberRead(
        ExpressionSyntax expression,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out ExpressionSyntax? memberSource)
    {
        memberSource = null;
        expression = SkipTransparentWrappers(expression);

        // box?.Value / box?.Inner.Value — same aggregate provenance as ordinary member
        // access (Codex P1 on #3441, PRRT_kwDORD0nYs602M2n).
        if (expression is ConditionalAccessExpressionSyntax conditionalAccess)
        {
            return TryResolveAggregateMemberReadFromConditionalWhenNotNull(
                conditionalAccess.Expression,
                SkipTransparentWrappers(conditionalAccess.WhenNotNull),
                aliases,
                semanticModel,
                root,
                cancellationToken,
                out memberSource);
        }

        if (expression is not MemberAccessExpressionSyntax memberAccess ||
            !memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression))
        {
            return false;
        }

        var receiver = SkipTransparentWrappers(memberAccess.Expression);
        if (TryResolveAggregateMemberRead(
                receiver, aliases, semanticModel, root, cancellationToken, out var resolvedReceiver))
        {
            receiver = resolvedReceiver;
        }

        if (!TryResolvePatternMemberInput(
                receiver,
                memberAccess.Name.Identifier.ValueText,
                aliases,
                semanticModel,
                root,
                cancellationToken,
                out var resolved) ||
            resolved == null)
        {
            return false;
        }

        memberSource = resolved;
        return true;
    }

    /// <summary>
    /// Resolves a null-conditional member chain (<c>box?.Value</c> /
    /// <c>box?.Inner.Value</c>) to the aggregate initializer expression it reads.
    /// </summary>
    private static bool TryResolveAggregateMemberReadFromConditionalWhenNotNull(
        ExpressionSyntax receiver,
        ExpressionSyntax whenNotNull,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        SyntaxNode root,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out ExpressionSyntax? memberSource)
    {
        memberSource = null;
        whenNotNull = SkipTransparentWrappers(whenNotNull);

        switch (whenNotNull)
        {
            case MemberBindingExpressionSyntax memberBinding:
                {
                    receiver = SkipTransparentWrappers(receiver);
                    if (TryResolveAggregateMemberRead(
                            receiver, aliases, semanticModel, root, cancellationToken, out var resolvedReceiver))
                    {
                        receiver = resolvedReceiver;
                    }

                    if (!TryResolvePatternMemberInput(
                            receiver,
                            memberBinding.Name.Identifier.ValueText,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken,
                            out var resolved) ||
                        resolved == null)
                    {
                        return false;
                    }

                    memberSource = resolved;
                    return true;
                }
            case MemberAccessExpressionSyntax memberAccess when
                memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                {
                    // box?.Inner.Value — resolve the binding prefix, then the final member.
                    if (!TryResolveAggregateMemberReadFromConditionalWhenNotNull(
                            receiver,
                            memberAccess.Expression,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken,
                            out var mid) ||
                        mid == null)
                    {
                        return false;
                    }

                    if (!TryResolvePatternMemberInput(
                            mid,
                            memberAccess.Name.Identifier.ValueText,
                            aliases,
                            semanticModel,
                            root,
                            cancellationToken,
                            out var resolved) ||
                        resolved == null)
                    {
                        return false;
                    }

                    memberSource = resolved;
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Member-name path a property subpattern binds against: <c>Value</c> for
    /// <c>{ Value: p }</c>, <c>Inner, Value</c> for the extended property pattern
    /// <c>{ Inner.Value: p }</c> (Codex P1 on #3441).
    /// </summary>
    private static bool TryGetPropertySubpatternMemberPath(
        SubpatternSyntax subpattern,
        out IReadOnlyList<string> memberPath)
    {
        if (subpattern.NameColon != null)
        {
            memberPath = [subpattern.NameColon.Name.Identifier.ValueText];
            return true;
        }

        var names = new List<string>();
        if (subpattern.ExpressionColon != null &&
            TryCollectMemberPath(subpattern.ExpressionColon.Expression, names))
        {
            memberPath = names;
            return true;
        }

        memberPath = [];
        return false;
    }

    private static bool TryCollectMemberPath(ExpressionSyntax expression, List<string> names)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                names.Add(identifier.Identifier.ValueText);
                return true;
            case MemberAccessExpressionSyntax memberAccess when
                memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                if (!TryCollectMemberPath(memberAccess.Expression, names))
                    return false;
                names.Add(memberAccess.Name.Identifier.ValueText);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Strips one value-transparent wrapper: parentheses, null-forgiving
    /// <c>x!</c>, <c>checked(x)</c> / <c>unchecked(x)</c>, struct <c>with</c>
    /// expressions (<c>point with { Y = 3 }</c> — receiver is the storage source),
    /// and simple assignment expressions used as values (<c>(tmp = point)</c>)
    /// (Codex P1 on #3441).
    /// </summary>
    private static bool TryUnwrapTransparentWrapper(ExpressionSyntax expression, out ExpressionSyntax inner)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                inner = parenthesized.Expression;
                return true;
            case PostfixUnaryExpressionSyntax postfix when
                postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                inner = postfix.Operand;
                return true;
            case CheckedExpressionSyntax checkedExpression:
                inner = checkedExpression.Expression;
                return true;
            // var copy = point with { Y = 3 }; — with creates a modified copy of the
            // receiver; track the receiver as the storage source (Codex P1).
            case WithExpressionSyntax withExpression:
                inner = withExpression.Expression;
                return true;
            // var copy = (tmp = point); — assignment-as-value yields the RHS.
            case AssignmentExpressionSyntax assignment when
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                inner = assignment.Right;
                return true;
            default:
                inner = expression;
                return false;
        }
    }

    /// <summary>
    /// Unwraps one built-in identity or boxing cast / <c>as</c> (e.g. <c>(dynamic)point</c> /
    /// <c>(object)point</c> / <c>point as object</c>). User-defined conversions are not
    /// followed so a custom conversion cannot invent a false alias (Codex P1 on #3441).
    /// </summary>
    private static bool TryUnwrapValuePreservingCast(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ExpressionSyntax operand)
    {
        operand = expression;

        ExpressionSyntax? candidate = null;
        if (expression is CastExpressionSyntax cast)
            candidate = cast.Expression;
        else if (expression is BinaryExpressionSyntax asExpression &&
                 asExpression.IsKind(SyntaxKind.AsExpression))
            candidate = asExpression.Left;
        else
            return false;

        var conversion = semanticModel.GetConversion(expression, cancellationToken);
        if (!conversion.IsIdentity && !conversion.IsBoxing)
            return false;

        operand = candidate;
        return true;
    }

    /// <summary>
    /// Unwraps one pointer-derived alias source to its pointer operand:
    /// <c>p + 0</c> / <c>p - 0</c>, <c>++p</c> / <c>p++</c> / <c>--p</c> / <c>p--</c>,
    /// pointer casts <c>(T*)p</c>, and compound <c>p += n</c> / <c>p -= n</c>
    /// (Codex P1 on #3441). Address-of is handled separately.
    /// </summary>
    private static bool TryUnwrapPointerDerivedSource(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out ExpressionSyntax pointerOperand)
    {
        switch (expression)
        {
            case BinaryExpressionSyntax binary when
                binary.IsKind(SyntaxKind.AddExpression) ||
                binary.IsKind(SyntaxKind.SubtractExpression):
                if (IsPointerTyped(binary.Left, semanticModel))
                {
                    pointerOperand = binary.Left;
                    return true;
                }

                if (IsPointerTyped(binary.Right, semanticModel))
                {
                    pointerOperand = binary.Right;
                    return true;
                }

                break;

            case PrefixUnaryExpressionSyntax prefix when
                prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                prefix.IsKind(SyntaxKind.PreDecrementExpression):
                if (IsPointerTyped(prefix.Operand, semanticModel))
                {
                    pointerOperand = prefix.Operand;
                    return true;
                }

                break;

            case PostfixUnaryExpressionSyntax postfix when
                postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                postfix.IsKind(SyntaxKind.PostDecrementExpression):
                if (IsPointerTyped(postfix.Operand, semanticModel))
                {
                    pointerOperand = postfix.Operand;
                    return true;
                }

                break;

            case CastExpressionSyntax cast:
                // (T*)p / (void*)p — follow when the operand or result is a pointer.
                if (IsPointerTyped(cast.Expression, semanticModel) ||
                    semanticModel.GetTypeInfo(cast).Type is IPointerTypeSymbol)
                {
                    pointerOperand = cast.Expression;
                    return true;
                }

                break;

            case AssignmentExpressionSyntax assignment when
                assignment.IsKind(SyntaxKind.AddAssignmentExpression) ||
                assignment.IsKind(SyntaxKind.SubtractAssignmentExpression):
                if (IsPointerTyped(assignment.Left, semanticModel))
                {
                    pointerOperand = assignment.Left;
                    return true;
                }

                break;
        }

        pointerOperand = expression;
        return false;
    }

    private static bool IsPointerTyped(ExpressionSyntax expression, SemanticModel semanticModel) =>
        semanticModel.GetTypeInfo(expression).Type is IPointerTypeSymbol;

    private static ExpressionSyntax SkipTransparentWrappers(ExpressionSyntax expression)
    {
        while (TryUnwrapTransparentWrapper(expression, out var inner))
            expression = inner;
        return expression;
    }

    private static PatternSyntax UnwrapParenthesizedPattern(PatternSyntax pattern)
    {
        while (pattern is ParenthesizedPatternSyntax parenthesized)
            pattern = parenthesized.Pattern;
        return pattern;
    }

    private static ExpressionSyntax UnwrapParenthesesAndRef(ExpressionSyntax expression)
    {
        while (true)
        {
            if (TryUnwrapTransparentWrapper(expression, out var inner))
                expression = inner;
            else if (expression is RefExpressionSyntax refExpression)
                expression = refExpression.Expression;
            else
                return expression;
        }
    }

    /// <summary>
    /// True when <paramref name="expression"/> is (or is a conditional /
    /// null-coalescing / switch / identity-like generic invocation / array-or-collection
    /// creation / container element read / container-forwarding call /
    /// storage-returning-delegate selector that forwards) a reference to a known
    /// storage alias.
    /// </summary>
    internal static bool ExpressionReferencesStorageAlias(
        ExpressionSyntax expression,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Parentheses, null-forgiving point!, checked(point) (Codex P1 on #3441).
            if (TryUnwrapTransparentWrapper(expression, out var inner))
                expression = inner;
            else if (expression is RefExpressionSyntax refExpression)
                expression = refExpression.Expression;
            // Pointer indirection (*p).X — follow back to the tracked pointer alias
            // (Codex P1 on #3441). p->X already hits ReceiverYieldsConvertedStorage's
            // IPointerTypeSymbol path; (*p) has no symbol of its own.
            else if (expression is PrefixUnaryExpressionSyntax indirection &&
                     indirection.IsKind(SyntaxKind.PointerIndirectionExpression))
                expression = indirection.Operand;
            // Pointer address-of / arithmetic / cast / ++/-- / compound += (Codex P1).
            else if (expression is PrefixUnaryExpressionSyntax addressOf &&
                     addressOf.IsKind(SyntaxKind.AddressOfExpression))
                expression = addressOf.Operand;
            else if (TryUnwrapPointerDerivedSource(expression, semanticModel, out var pointerOperand))
                expression = pointerOperand;
            // Built-in identity/boxing casts / as: (dynamic)point / point as object (Codex P1).
            else if (TryUnwrapValuePreservingCast(
                         expression, semanticModel, cancellationToken, out var castOperand))
                expression = castOperand;
            else
                break;
        }

        // Alias-like seed value (the selected creation / an opaque-return call-site
        // result) used directly at this site (Codex P1s on #3441,
        // PRRT_kwDORD0nYs605__Q / …605__Y / …605__c).
        if (IsSeedValue(aliases, expression))
            return true;

        var symbol = semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;
        if (symbol != null && aliases.Contains(symbol))
            return true;

        // GetSymbolInfo returns null for conditionals / switches; walk arms so
        // var copy = cond ? point : point; and switch { true => point, ... }
        // still alias copy to point.
        if (expression is ConditionalExpressionSyntax conditional)
        {
            return ExpressionReferencesStorageAlias(conditional.WhenTrue, aliases, semanticModel, cancellationToken)
                || ExpressionReferencesStorageAlias(conditional.WhenFalse, aliases, semanticModel, cancellationToken);
        }

        // Null-coalescing: var copy = maybe ?? point (Codex P1 on #3441).
        if (expression is BinaryExpressionSyntax coalesce &&
            coalesce.IsKind(SyntaxKind.CoalesceExpression))
        {
            return ExpressionReferencesStorageAlias(coalesce.Left, aliases, semanticModel, cancellationToken)
                || ExpressionReferencesStorageAlias(coalesce.Right, aliases, semanticModel, cancellationToken);
        }

        if (expression is SwitchExpressionSyntax switchExpression)
        {
            foreach (var arm in switchExpression.Arms)
            {
                if (ExpressionReferencesStorageAlias(arm.Expression, aliases, semanticModel, cancellationToken))
                    return true;
            }
        }

        // Generic / identity-like invocations that take an alias and return the
        // same parameter type (e.g. static T Echo<T>(T value) => value) so
        // var copy = Echo(point); copy.X = 3; is rejected (CS8852).
        // Named arguments bind by parameter name (Pick(value: point, other: 0)).
        // Also extension / instance receivers (points.ToArray() / First()) whose
        // return forwards the container or its element type.
        if (expression is InvocationExpressionSyntax invocation &&
            semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method)
        {
            var args = invocation.ArgumentList.Arguments;
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (!ExpressionReferencesStorageAlias(arg.Expression, aliases, semanticModel, cancellationToken))
                    continue;

                var parameter = ResolveInvocationParameter(method, arg, i);
                if (parameter == null)
                    continue;

                if (SymbolEqualityComparer.Default.Equals(parameter.Type, method.ReturnType))
                {
                    // Array.ConvertAll / Select: same constructed types do not
                    // imply aliasing when the converter synthesizes fresh values.
                    if (RequiresIdentityConverterToForward(method))
                    {
                        if (IsIdentitySelectInvocation(invocation, method, semanticModel, cancellationToken) ||
                            IsIdentityConvertAllInvocation(invocation, method, semanticModel, cancellationToken) ||
                            IsStorageReturningDelegateSelectorInvocation(invocation, method, semanticModel, cancellationToken))
                            return true;
                        continue;
                    }

                    return true;
                }

                // Enumerable.ToArray<T>(IEnumerable<T>): parameter is sequence,
                // return is T[] — preserve when element types match / forward.
                if (MethodReturnForwardsContainer(
                        method,
                        semanticModel.GetTypeInfo(arg.Expression, cancellationToken).Type))
                {
                    return true;
                }

                // Enumerable.Repeat(point, n) / similar: argument is element T,
                // return is IEnumerable<T> (Codex P1 sequence factories).
                if (MethodReturnWrapsElementAsSequence(method, parameter))
                    return true;

                // Task.FromResult(point) / similar: argument is T, return is G<T>.
                if (MethodReturnWrapsElementAsConstructedGeneric(method, parameter, aliases))
                    return true;

                // Opaque MakeGetter(point) / factory(point) whose return is a
                // storage-returning delegate (Func<T> / nested Func<Func<T>>) —
                // including non-generic signatures MethodReturnWrapsElementAsConstructedGeneric
                // misses (Codex P1 on #3441).
                if (MethodReturnWrapsElementAsStorageReturningDelegate(method, parameter))
                    return true;
            }

            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                ExpressionReferencesStorageAlias(
                    memberAccess.Expression,
                    aliases,
                    semanticModel,
                    cancellationToken) &&
                (MethodReturnForwardsContainer(
                     method,
                     semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type) ||
                 MethodReturnExtractsReceiverStorage(method, aliases) ||
                 IsIdentitySelectInvocation(invocation, method, semanticModel, cancellationToken) ||
                 IsStorageReturningDelegateSelectorInvocation(invocation, method, semanticModel, cancellationToken)))
            {
                return true;
            }

            // points.Select(x => x) — TSource/TResult differ but selector forwards.
            // Also points.Select(x => () => x) — selector returns a storage-returning
            // delegate carrier (Codex P1 on #3441).
            if (IsIdentitySelectInvocation(invocation, method, semanticModel, cancellationToken) ||
                IsStorageReturningDelegateSelectorInvocation(invocation, method, semanticModel, cancellationToken))
            {
                if (invocation.Expression is MemberAccessExpressionSyntax receiverAccess &&
                    ExpressionReferencesStorageAlias(
                        receiverAccess.Expression,
                        aliases,
                        semanticModel,
                        cancellationToken))
                {
                    return true;
                }

                foreach (var arg in args)
                {
                    if (ExpressionReferencesStorageAlias(arg.Expression, aliases, semanticModel, cancellationToken))
                        return true;
                }
            }

            // Invoking a tracked delegate / local function that returns converted storage
            // (var get = () => point; var copy = get();) (Codex P1 on #3441).
            if (!method.ReturnsVoid &&
                ExpressionReferencesStorageAlias(
                    invocation.Expression, aliases, semanticModel, cancellationToken))
            {
                return true;
            }

            // Explicit .Invoke on a tracked delegate: get.Invoke() (Codex P1 on #3441).
            if (!method.ReturnsVoid &&
                invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Invoke"
                } invokeAccess &&
                ExpressionReferencesStorageAlias(
                    invokeAccess.Expression, aliases, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        // Array / collection creations that embed an alias so
        // var points = new[] { point }; points[0].X = 3; tracks points.
        // Also stackalloc[] { point } / stackalloc T[n] { point } (Codex P1 on #3441).
        if (expression is ImplicitArrayCreationExpressionSyntax implicitArray &&
            implicitArray.Initializer != null)
        {
            foreach (var element in implicitArray.Initializer.Expressions)
            {
                if (ExpressionReferencesStorageAlias(element, aliases, semanticModel, cancellationToken))
                    return true;
            }
        }

        if (expression is ArrayCreationExpressionSyntax arrayCreation &&
            arrayCreation.Initializer != null)
        {
            foreach (var element in arrayCreation.Initializer.Expressions)
            {
                if (ExpressionReferencesStorageAlias(element, aliases, semanticModel, cancellationToken))
                    return true;
            }
        }

        if (expression is ImplicitStackAllocArrayCreationExpressionSyntax implicitStackAlloc &&
            implicitStackAlloc.Initializer != null)
        {
            foreach (var element in implicitStackAlloc.Initializer.Expressions)
            {
                if (ExpressionReferencesStorageAlias(element, aliases, semanticModel, cancellationToken))
                    return true;
            }
        }

        if (expression is StackAllocArrayCreationExpressionSyntax stackAlloc &&
            stackAlloc.Initializer != null)
        {
            foreach (var element in stackAlloc.Initializer.Expressions)
            {
                if (ExpressionReferencesStorageAlias(element, aliases, semanticModel, cancellationToken))
                    return true;
            }
        }

        // A declaration such as (int X, int Y)[] points = { point } has a
        // bare array initializer, not an ArrayCreationExpressionSyntax. Track
        // its elements just like explicit new T[] and inferred new[] arrays.
        if (expression is InitializerExpressionSyntax arrayInitializer &&
            arrayInitializer.IsKind(SyntaxKind.ArrayInitializerExpression))
        {
            foreach (var element in arrayInitializer.Expressions)
            {
                if (ExpressionReferencesStorageAlias(element, aliases, semanticModel, cancellationToken))
                    return true;
            }
        }

        if (expression is CollectionExpressionSyntax collection)
        {
            foreach (var element in collection.Elements)
            {
                // Expression elements: [point] / [point, other]
                // Spread elements: [.. points] — otherwise copies = [.. points] skips
                // the carrier and later dynamic ref bindings miss the gate (Codex P1).
                ExpressionSyntax? elementExpression = element switch
                {
                    ExpressionElementSyntax expressionElement => expressionElement.Expression,
                    SpreadElementSyntax spreadElement => spreadElement.Expression,
                    _ => null
                };
                if (elementExpression != null &&
                    ExpressionReferencesStorageAlias(elementExpression, aliases, semanticModel, cancellationToken))
                {
                    return true;
                }
            }
        }

        // Ordinary constructors that capture converted storage into object / dynamic /
        // generic parameters are storage carriers (Codex P1 on #3441,
        // PRRT_kwDORD0nYs602oDk): var holder = new Holder(point) with
        // Holder(object value) storing into dynamic Value.
        // Explicit collection containers also carry converted values. Their generic
        // element type may stay a tuple, but by-ref callbacks must still refuse
        // before replacing the initializer value with an incompatible struct.
        if (expression is BaseObjectCreationExpressionSyntax objectCreation)
        {
            if (ObjectCreationCapturesConvertedStorageViaConstructor(
                    objectCreation, aliases, semanticModel, cancellationToken))
            {
                return true;
            }

            if (objectCreation.Initializer is { } initializer &&
                initializer.IsKind(SyntaxKind.CollectionInitializerExpression))
            {
                foreach (var element in initializer.Expressions)
                {
                    // Bind each entry to the collection initializer's Add method and only
                    // count parts flowing into parameters whose type embeds converted
                    // storage — { { point, value } } into Dictionary<object, V> only puts
                    // point in the object key (Codex P2 on #3441, PRRT_kwDORD0nYs600gmf).
                    // Mirrors TryAddCollectionInsertionReceiverAlias for explicit Add calls.
                    var addMethod = semanticModel.GetCollectionInitializerSymbolInfo(element, cancellationToken).Symbol
                        as IMethodSymbol;

                    // new Dictionary<K, V> { { key, point } }: complex element initializers.
                    if (element is InitializerExpressionSyntax complexElement &&
                        complexElement.IsKind(SyntaxKind.ComplexElementInitializerExpression))
                    {
                        for (var i = 0; i < complexElement.Expressions.Count; i++)
                        {
                            if (!CollectionInitializerPartMayCarryStorage(addMethod, i, aliases))
                                continue;

                            if (ExpressionReferencesStorageAlias(
                                    complexElement.Expressions[i], aliases, semanticModel, cancellationToken))
                            {
                                return true;
                            }
                        }

                        continue;
                    }

                    if (CollectionInitializerPartMayCarryStorage(addMethod, 0, aliases) &&
                        ExpressionReferencesStorageAlias(element, aliases, semanticModel, cancellationToken))
                    {
                        return true;
                    }
                }
            }
        }

        // Nested tuple containers: var wrapped = (Value: point, Other: 0);
        if (expression is TupleExpressionSyntax tupleExpression)
        {
            foreach (var argument in tupleExpression.Arguments)
            {
                if (ExpressionReferencesStorageAlias(
                        argument.Expression, aliases, semanticModel, cancellationToken))
                {
                    return true;
                }
            }
        }

        // Nested anonymous-object containers: var box = new { Value = point };
        if (expression is AnonymousObjectCreationExpressionSyntax anonymousObject)
        {
            foreach (var member in anonymousObject.Initializers)
            {
                if (ExpressionReferencesStorageAlias(
                        member.Expression, aliases, semanticModel, cancellationToken))
                {
                    return true;
                }
            }
        }

        // Reads from tracked containers: var copy = points[0]; copy.X = 3.
        // Prefer constant-index resolution against initializer-known containers before
        // falling back to container-wide propagation (Codex P2 on #3441,
        // PRRT_kwDORD0nYs602oDm): object[] values = { point, new Other() };
        // dynamic copy = values[1]; must not taint copy.
        if (expression is ElementAccessExpressionSyntax elementAccess)
        {
            if (TryResolveConstantIndexedContainerElement(
                    elementAccess, aliases, semanticModel, cancellationToken, out var indexedElement) &&
                indexedElement != null)
            {
                if (ExpressionReferencesStorageAlias(
                        indexedElement, aliases, semanticModel, cancellationToken))
                {
                    return true;
                }

                // Unrelated sibling (new Other() beside point): do not taint via the
                // container (Codex P2). Nested converted tuple literals such as
                // dynamic[] points = { (1, 2) }; Bump(ref points[0].Item1) still need
                // container-wide fallthrough — the enclosing array is the tracked
                // storage for that nested creation.
                if (!ElementMayBeNestedConvertedCreation(indexedElement))
                    return false;
            }

            return ExpressionReferencesStorageAlias(
                elementAccess.Expression, aliases, semanticModel, cancellationToken);
        }

        // Forwarding members only. Do not treat every member of an
        // alias-containing aggregate as an alias (wrapped.Fresh) — Codex P2.
        if (expression is MemberAccessExpressionSyntax nestedAccess)
        {
            var memberName = nestedAccess.Name.Identifier.ValueText;
            // Span/Memory: container views. Result: Task<T>/ValueTask<T> sync unwrap.
            if (memberName is "Span" or "Memory" or "Result")
            {
                return ExpressionReferencesStorageAlias(
                    nestedAccess.Expression, aliases, semanticModel, cancellationToken);
            }

            // Generic wrapper unwrap: Box<T>.Value where T matches tracked storage
            // (Codex P1 on #3441). Tuples/anonymous carriers use the creation walk.
            if (MemberAccessUnwrapsStorageTypeArgument(
                    nestedAccess, aliases, semanticModel, cancellationToken) &&
                ExpressionReferencesStorageAlias(
                    nestedAccess.Expression, aliases, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        // Await unwrap: var copy = await Task.FromResult(point);
        if (expression is AwaitExpressionSyntax awaitExpression)
        {
            return ExpressionReferencesStorageAlias(
                awaitExpression.Expression, aliases, semanticModel, cancellationToken);
        }

        // Query results: var query = from x in new[] { point } select x;
        // so foreach (var copy in query) tracks query as carrying converted storage
        // (Codex P1). Range variables themselves are collected via FromClause/etc.
        if (expression is QueryExpressionSyntax queryExpression)
        {
            return QueryProjectionYieldsConvertedStorage(
                queryExpression, aliases, semanticModel, cancellationToken);
        }

        // Inferred delegates whose return yields converted storage:
        // var get = () => point; (Codex P1 on #3441).
        if (expression is AnonymousFunctionExpressionSyntax anonymousFunction)
        {
            return AnonymousFunctionReturnsConvertedStorage(
                anonymousFunction, aliases, semanticModel, cancellationToken);
        }

        // Null-conditional member/element bindings: box?.Value / points?[0]
        // (Codex P1 on #3441, PRRT_kwDORD0nYs602M2n).
        if (expression is ConditionalAccessExpressionSyntax conditionalAccess)
        {
            return ConditionalAccessReferencesStorageAlias(
                conditionalAccess, aliases, semanticModel, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// True when a null-conditional expression forwards converted storage the same
    /// way the equivalent ordinary member/element/invocation access would
    /// (<c>box?.Value</c>, <c>points?[0]</c>, <c>box?.Get()</c>).
    /// </summary>
    private static bool ConditionalAccessReferencesStorageAlias(
        ConditionalAccessExpressionSyntax conditionalAccess,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        ExpressionReferencesStorageThroughConditionalWhenNotNull(
            conditionalAccess.Expression,
            conditionalAccess.WhenNotNull,
            conditionalAccess,
            aliases,
            semanticModel,
            cancellationToken);

    /// <summary>
    /// Walks the <c>WhenNotNull</c> side of a null-conditional chain as if each
    /// member/element binding were an ordinary access rooted at <paramref name="receiver"/>.
    /// </summary>
    private static bool ExpressionReferencesStorageThroughConditionalWhenNotNull(
        ExpressionSyntax receiver,
        ExpressionSyntax whenNotNull,
        ExpressionSyntax conditionalRoot,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        whenNotNull = SkipTransparentWrappers(whenNotNull);

        switch (whenNotNull)
        {
            case MemberBindingExpressionSyntax memberBinding:
                {
                    var memberName = memberBinding.Name.Identifier.ValueText;
                    if (memberName is "Span" or "Memory" or "Result")
                    {
                        return ExpressionReferencesStorageAlias(
                            receiver, aliases, semanticModel, cancellationToken);
                    }

                    // box?.Value — same wrapper-member provenance as box.Value.
                    return MemberAccessUnwrapsStorageTypeArgument(
                               conditionalRoot,
                               receiver,
                               memberBinding.Name,
                               aliases,
                               semanticModel,
                               cancellationToken) &&
                           ExpressionReferencesStorageAlias(
                               receiver, aliases, semanticModel, cancellationToken);
                }
            case ElementBindingExpressionSyntax:
                // points?[0]
                return ExpressionReferencesStorageAlias(
                    receiver, aliases, semanticModel, cancellationToken);
            case MemberAccessExpressionSyntax memberAccess when
                memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                {
                    // box?.Inner.Value
                    if (!ExpressionReferencesStorageThroughConditionalWhenNotNull(
                            receiver,
                            memberAccess.Expression,
                            conditionalRoot,
                            aliases,
                            semanticModel,
                            cancellationToken))
                    {
                        return false;
                    }

                    var memberName = memberAccess.Name.Identifier.ValueText;
                    if (memberName is "Span" or "Memory" or "Result")
                        return true;

                    return MemberAccessUnwrapsStorageTypeArgument(
                        memberAccess,
                        memberAccess.Expression,
                        memberAccess.Name,
                        aliases,
                        semanticModel,
                        cancellationToken);
                }
            case ElementAccessExpressionSyntax elementAccess:
                // box?.Values[0]
                return ExpressionReferencesStorageThroughConditionalWhenNotNull(
                    receiver,
                    elementAccess.Expression,
                    conditionalRoot,
                    aliases,
                    semanticModel,
                    cancellationToken);
            case InvocationExpressionSyntax invocation:
                {
                    // box?.Get() / box?.Unwrap() — extract storage from the conditional receiver.
                    if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
                        method.ReturnsVoid)
                    {
                        return false;
                    }

                    if (!ExpressionReferencesStorageAlias(
                            receiver, aliases, semanticModel, cancellationToken))
                    {
                        return false;
                    }

                    return MethodReturnExtractsReceiverStorage(method, aliases) ||
                           MethodReturnForwardsContainer(
                               method,
                               semanticModel.GetTypeInfo(receiver, cancellationToken).Type);
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves the <see cref="IParameterSymbol"/> bound by <paramref name="argument"/>,
    /// honoring named arguments (Codex P1).
    /// </summary>
    private static IParameterSymbol? ResolveInvocationParameter(
        IMethodSymbol method,
        ArgumentSyntax argument,
        int positionalIndex)
    {
        if (argument.NameColon != null)
        {
            var name = argument.NameColon.Name.Identifier.ValueText;
            foreach (var parameter in method.Parameters)
            {
                if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
                    return parameter;
            }

            return null;
        }

        return positionalIndex < method.Parameters.Length
            ? method.Parameters[positionalIndex]
            : null;
    }

    /// <summary>
    /// True when <paramref name="method"/>'s return forwards a container
    /// (same type, First/Single element, or a sequence/array projection that
    /// preserves the source element type parameter — not Select to fresh tuples).
    /// </summary>
    private static bool MethodReturnForwardsContainer(IMethodSymbol method, ITypeSymbol? sourceType)
    {
        if (sourceType == null || method.ReturnsVoid)
            return false;

        var returnType = method.ReturnType;
        if (SymbolEqualityComparer.Default.Equals(returnType, sourceType))
            return true;

        var sourceElement = TryGetSequenceElementType(sourceType);
        if (sourceElement == null)
            return false;

        // First()/Single()/ElementAt(): return type is the sequence element.
        // Restrict by name so Aggregate/etc. that synthesize a fresh element
        // are not treated as forwarding (Copilot).
        if (SymbolEqualityComparer.Default.Equals(returnType, sourceElement) &&
            IsElementExtractingMethod(method))
        {
            return true;
        }

        var returnElement = TryGetSequenceElementType(returnType);
        if (returnElement == null ||
            !SymbolEqualityComparer.Default.Equals(sourceElement, returnElement))
        {
            return false;
        }

        // Constructed element types match — also require the original method's
        // return element type parameter to be the same as the source sequence's
        // element type parameter (ToArray/AsSpan), not a distinct TResult
        // (Select) that happens to construct to the same tuple shape (Codex P2).
        // Identity Select(x => x) is handled separately via IsIdentitySelectInvocation.
        return OriginalReturnElementSharesSourceElementTypeParameter(method);
    }

    /// <summary>
    /// True when collection-initializer part <paramref name="index"/> binds to an
    /// <c>Add</c> parameter whose type embeds converted storage. Refusal-leaning:
    /// unresolved Add methods, params arrays, and out-of-range parts count.
    /// </summary>
    private static bool CollectionInitializerPartMayCarryStorage(
        IMethodSymbol? addMethod,
        int index,
        HashSet<ISymbol> aliases)
    {
        if (addMethod == null)
            return true;

        var parameters = addMethod.Parameters;
        if (parameters.Length == 0)
            return true;

        IParameterSymbol parameter;
        if (index < parameters.Length)
        {
            parameter = parameters[index];
        }
        else if (parameters[^1].IsParams)
        {
            parameter = parameters[^1];
        }
        else
        {
            return true;
        }

        if (parameter.IsParams)
            return true;

        return TypeInvolvesStorageAliasType(parameter.Type, aliases);
    }

    /// <summary>
    /// True when <paramref name="memberAccess"/> yields a constructed type argument
    /// of its receiver that matches tracked storage (e.g. <c>Box&lt;T&gt;.Value</c>
    /// where <c>T</c> is the converted tuple). Skips tuple/anonymous carriers, which
    /// use the aggregate-creation walk instead (including invocation returns that
    /// embed <c>T</c> — see <see cref="EnumerateInvocationForwardedMemberArguments"/>).
    /// </summary>
    private static bool MemberAccessUnwrapsStorageTypeArgument(
        MemberAccessExpressionSyntax memberAccess,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        MemberAccessUnwrapsStorageTypeArgument(
            memberAccess,
            memberAccess.Expression,
            memberAccess.Name,
            aliases,
            semanticModel,
            cancellationToken);

    /// <summary>
    /// Shared unwrap check for ordinary member access and null-conditional member
    /// bindings (<c>box?.Value</c>) — <paramref name="memberExpression"/> supplies
    /// the member type (the conditional access node when lifted), while
    /// <paramref name="receiverExpression"/> / <paramref name="memberName"/> supply
    /// the receiver type and member symbol (Codex P1 on #3441).
    /// </summary>
    private static bool MemberAccessUnwrapsStorageTypeArgument(
        ExpressionSyntax memberExpression,
        ExpressionSyntax receiverExpression,
        SimpleNameSyntax memberName,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var memberType = semanticModel.GetTypeInfo(memberExpression, cancellationToken).Type;
        if (memberType == null || !TypeInvolvesStorageAliasType(memberType, aliases))
            return false;

        var receiverType = semanticModel.GetTypeInfo(receiverExpression, cancellationToken).Type;
        if (receiverType is not INamedTypeSymbol { IsTupleType: false, IsAnonymousType: false } named ||
            named.TypeArguments.Length == 0)
        {
            return false;
        }

        // Fields/properties: only members whose original declared type mentions a
        // containing-type parameter bound to storage forward it. Box<T>.Other declared
        // as (int X, int Y) merely shares the constructed shape (Codex P2 on #3441,
        // PRRT_kwDORD0nYs600gme). Other member kinds keep the constructed-type check.
        var member = semanticModel.GetSymbolInfo(memberName, cancellationToken).Symbol
            ?? semanticModel.GetSymbolInfo(memberExpression, cancellationToken).Symbol;
        if (member is IFieldSymbol or IPropertySymbol)
            return MemberOriginalTypeForwardsStorageTypeArgument(member, memberType);

        foreach (var argument in named.TypeArguments)
        {
            // Direct unwrap: Box<T>.Value where memberType == T.
            // Nested: Box<T>.Values where memberType is T[] (or other nesting) —
            // exact equality rejects forwarding; TypeContainsOrEquals recognizes
            // the receiver type argument inside the member type (Codex P1).
            if (SymbolEqualityComparer.Default.Equals(argument, memberType) ||
                TypeContainsOrEquals(memberType, argument))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the original definition of field/property <paramref name="member"/>
    /// declares its type in terms of a containing-type parameter (including outer
    /// and base types) whose constructed argument appears in <paramref name="memberType"/>.
    /// </summary>
    private static bool MemberOriginalTypeForwardsStorageTypeArgument(
        ISymbol member,
        ITypeSymbol memberType)
    {
        var originalType = member.OriginalDefinition switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null
        };
        if (originalType == null)
            return true;

        for (var containing = member.ContainingType; containing != null; containing = containing.ContainingType)
        {
            var typeParameters = containing.OriginalDefinition.TypeParameters;
            var typeArguments = containing.TypeArguments;
            for (var i = 0; i < typeParameters.Length && i < typeArguments.Length; i++)
            {
                if (TypeContainsOrEquals(originalType, typeParameters[i]) &&
                    TypeContainsOrEquals(memberType, typeArguments[i]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="parameter"/> is type parameter <c>T</c> and the
    /// method return type contains <c>T</c> — sole-arg wrappers
    /// (<c>Task.FromResult&lt;T&gt;</c>), multi-arg generics, or tuple returns that
    /// embed <c>T</c> (e.g. <c>(T Value, int Other) Pair&lt;T&gt;(T value)</c>).
    /// </summary>
    private static bool MethodReturnWrapsElementAsConstructedGeneric(
        IMethodSymbol method,
        IParameterSymbol parameter,
        HashSet<ISymbol> aliases)
    {
        var original = (method.ReducedFrom ?? method).OriginalDefinition;
        // Reduced extension calls (5.With(point)) bind arguments to the reduced
        // parameter list; the original definition carries the receiver at 0.
        var ordinal = parameter.Ordinal + (method.ReducedFrom != null ? 1 : 0);
        if (ordinal < 0 || ordinal >= original.Parameters.Length)
            return false;

        var originalParameterType = original.Parameters[ordinal].Type;
        if (originalParameterType is ITypeParameterSymbol elementTypeParameter)
        {
            // Recurse into multi-arg generics / nested / tuple returns rather than
            // requiring Arity: 1 (Codex P1 on #3441).
            return TypeContainsOrEquals(original.ReturnType, elementTypeParameter);
        }

        // Inverse flow: storage nested in a parameter type (Box<T> / Pair<K, V>)
        // and extracted by the return type (T Unwrap<T>(Box<T>)) — only the
        // type parameters actually bound to converted storage count, so
        // extracting an unrelated wrapper type argument does not alias
        // (Codex P1 on #3441).
        var sourced = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        CollectStorageBoundTypeParameters(originalParameterType, parameter.Type, aliases, sourced);
        return sourced.Count > 0 && TypeContainsAnyMethodTypeParameter(original.ReturnType, sourced);
    }

    /// <summary>
    /// True when an extension or instance call on a storage-carrying receiver
    /// returns a type parameter bound to converted storage — e.g.
    /// <c>box.Unwrap()</c> with <c>T Unwrap&lt;T&gt;(this Box&lt;T&gt;)</c> or
    /// <c>box.Get()</c> on <c>Box&lt;T&gt;</c> (Codex P1 on #3441).
    /// </summary>
    private static bool MethodReturnExtractsReceiverStorage(
        IMethodSymbol method,
        HashSet<ISymbol> aliases)
    {
        var sourced = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        ITypeSymbol originalReturnType;

        if (method.ReducedFrom != null)
        {
            var original = method.ReducedFrom.OriginalDefinition;
            if (original.Parameters.Length == 0 || method.ReceiverType == null)
                return false;

            CollectStorageBoundTypeParameters(
                original.Parameters[0].Type, method.ReceiverType, aliases, sourced);
            originalReturnType = original.ReturnType;
        }
        else if (!method.IsStatic && method.ContainingType is { IsGenericType: true } containingType)
        {
            CollectStorageBoundTypeParameters(
                containingType.OriginalDefinition, containingType, aliases, sourced);
            originalReturnType = method.OriginalDefinition.ReturnType;
        }
        else
        {
            return false;
        }

        return sourced.Count > 0 && TypeContainsAnyMethodTypeParameter(originalReturnType, sourced);
    }

    /// <summary>
    /// Walks an original (open) type alongside its constructed form and collects
    /// the type parameters whose constructed type argument embeds a tracked
    /// storage alias type (e.g. <c>T</c> from <c>Box&lt;T&gt;</c> /
    /// <c>Box&lt;(int X, int Y)&gt;</c>).
    /// </summary>
    private static void CollectStorageBoundTypeParameters(
        ITypeSymbol originalType,
        ITypeSymbol constructedType,
        HashSet<ISymbol> aliases,
        HashSet<ITypeParameterSymbol> into)
    {
        switch (originalType)
        {
            case ITypeParameterSymbol typeParameter:
                if (TypeArgumentEmbedsStorageAliasType(constructedType, aliases))
                    into.Add(typeParameter);
                break;
            case IArrayTypeSymbol originalArray when constructedType is IArrayTypeSymbol constructedArray:
                CollectStorageBoundTypeParameters(
                    originalArray.ElementType, constructedArray.ElementType, aliases, into);
                break;
            case INamedTypeSymbol originalNamed when constructedType is INamedTypeSymbol constructedNamed &&
                                                     originalNamed.TypeArguments.Length == constructedNamed.TypeArguments.Length:
                for (var i = 0; i < originalNamed.TypeArguments.Length; i++)
                {
                    CollectStorageBoundTypeParameters(
                        originalNamed.TypeArguments[i], constructedNamed.TypeArguments[i], aliases, into);
                }

                break;
        }
    }

    /// <summary>
    /// True when <paramref name="parameter"/> is an element type parameter <c>T</c>
    /// and the method returns a sequence of <c>T</c> (e.g. <c>Enumerable.Repeat</c>).
    /// </summary>
    private static bool MethodReturnWrapsElementAsSequence(IMethodSymbol method, IParameterSymbol parameter)
    {
        var original = (method.ReducedFrom ?? method).OriginalDefinition;
        var ordinal = parameter.Ordinal + (method.ReducedFrom != null ? 1 : 0);
        if (ordinal < 0 || ordinal >= original.Parameters.Length)
            return false;

        if (original.Parameters[ordinal].Type is not ITypeParameterSymbol elementTypeParameter)
            return false;

        var returnElement = TryGetSequenceElementType(original.ReturnType);
        return returnElement != null &&
               SymbolEqualityComparer.Default.Equals(returnElement, elementTypeParameter);
    }

    private static bool IsElementExtractingMethod(IMethodSymbol method)
    {
        var name = (method.ReducedFrom ?? method).OriginalDefinition.Name;
        return name is "First" or "FirstOrDefault" or "Single" or "SingleOrDefault"
            or "Last" or "LastOrDefault" or "ElementAt" or "ElementAtOrDefault";
    }

    private static bool OriginalReturnElementSharesSourceElementTypeParameter(IMethodSymbol method)
    {
        // Prefer ReducedFrom for extension invocations (points.ToArray() / AsSpan()).
        var original = (method.ReducedFrom ?? method).OriginalDefinition;

        ITypeSymbol? originalSourceElement = null;
        foreach (var parameter in original.Parameters)
        {
            originalSourceElement = TryGetSequenceElementType(parameter.Type);
            if (originalSourceElement != null)
                break;
        }

        // Instance copies on the container itself: List<T>.ToArray() /
        // ImmutableArray<T>.ToBuilder() — the source element is the containing
        // type's own sequence element type parameter.
        if (originalSourceElement == null &&
            method.ReducedFrom == null &&
            !original.IsStatic &&
            original.ContainingType is { } containingType)
        {
            originalSourceElement = TryGetSequenceElementType(containingType);
        }

        if (originalSourceElement is not ITypeParameterSymbol sourceTypeParameter)
            return false;

        if (TryGetSequenceElementType(original.ReturnType) is ITypeParameterSymbol returnTypeParameter)
            return SymbolEqualityComparer.Default.Equals(sourceTypeParameter, returnTypeParameter);

        if (original.ReturnType is IArrayTypeSymbol array &&
            array.ElementType is ITypeParameterSymbol arrayElement)
        {
            return SymbolEqualityComparer.Default.Equals(sourceTypeParameter, arrayElement);
        }

        return false;
    }

    /// <summary>
    /// True for <c>Select</c>/<c>SelectMany</c> whose selector is a simple identity
    /// (<c>x =&gt; x</c>), so <c>points.Select(x =&gt; x).ToArray()</c> still aliases.
    /// </summary>
    private static bool IsIdentitySelectInvocation(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var name = (method.ReducedFrom ?? method).OriginalDefinition.Name;
        if (name is not ("Select" or "SelectMany"))
            return false;

        var lambdaIdentity = new List<bool>();
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var expr = SkipTransparentWrappers(argument.Expression);

            if (expr is SimpleLambdaExpressionSyntax simple)
            {
                var parameterName = simple.Parameter.Identifier.ValueText;
                lambdaIdentity.Add(
                    IsIdentityLambdaBody(simple.ExpressionBody, simple.Block, parameterName));
                continue;
            }

            if (expr is ParenthesizedLambdaExpressionSyntax paren &&
                paren.ParameterList.Parameters.Count >= 1)
            {
                // Select((x, index) => x): first parameter.
                // SelectMany result selector (row, item) => item: last parameter.
                var first = paren.ParameterList.Parameters[0].Identifier.ValueText;
                var last = paren.ParameterList.Parameters[^1].Identifier.ValueText;
                var identity =
                    IsIdentityLambdaBody(paren.ExpressionBody, paren.Block, first) ||
                    (paren.ParameterList.Parameters.Count > 1 &&
                     name == "SelectMany" &&
                     IsIdentityLambdaBody(paren.ExpressionBody, paren.Block, last));
                // Indexed Select: body must be the element (first) parameter.
                if (name == "Select" && paren.ParameterList.Parameters.Count > 1)
                {
                    identity = IsIdentityLambdaBody(paren.ExpressionBody, paren.Block, first);
                }

                lambdaIdentity.Add(identity);
                continue;
            }

            // Method-group selectors: points.Select(Echo) (Codex P1 on #3441).
            var groupForwards = MethodGroupSelectorForwardsElement(
                expr, considerLastParameter: name == "SelectMany", semanticModel, cancellationToken);
            if (groupForwards.HasValue)
                lambdaIdentity.Add(groupForwards.Value);
        }

        // Select: any identity selector. SelectMany: every lambda must be identity
        // so a fresh result selector does not false-alias (Codex P2).
        if (lambdaIdentity.Count == 0)
            return false;

        return name == "Select"
            ? lambdaIdentity.Any(x => x)
            : lambdaIdentity.All(x => x);
    }

    /// <summary>
    /// True for expression-bodied <c>x =&gt; x</c> / <c>x =&gt; (x)</c> or block-bodied
    /// <c>x =&gt; { return x; }</c> identity selectors (Copilot).
    /// </summary>

    private static bool RequiresIdentityConverterToForward(IMethodSymbol method)
    {
        var name = (method.ReducedFrom ?? method).OriginalDefinition.Name;
        return name is "ConvertAll" or "Select" or "SelectMany";
    }

    private static bool IsIdentityConvertAllInvocation(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var name = (method.ReducedFrom ?? method).OriginalDefinition.Name;
        if (name is not "ConvertAll")
            return false;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var expr = SkipTransparentWrappers(argument.Expression);

            if (expr is SimpleLambdaExpressionSyntax simple &&
                IsIdentityLambdaBody(simple.ExpressionBody, simple.Block, simple.Parameter.Identifier.ValueText))
                return true;

            if (expr is ParenthesizedLambdaExpressionSyntax paren &&
                paren.ParameterList.Parameters.Count >= 1 &&
                IsIdentityLambdaBody(
                    paren.ExpressionBody,
                    paren.Block,
                    paren.ParameterList.Parameters[0].Identifier.ValueText))
                return true;

            // Method-group converters: Array.ConvertAll(points, Echo) (Codex P1 on #3441).
            if (MethodGroupSelectorForwardsElement(
                    expr, considerLastParameter: false, semanticModel, cancellationToken) == true)
                return true;
        }

        return false;
    }

    /// <summary>
    /// For a selector argument that is a method group (<c>points.Select(Echo)</c>),
    /// returns whether the referenced method forwards its element parameter's
    /// storage: a generic return that mentions the element parameter's type
    /// parameters (<c>T Echo&lt;T&gt;(T)</c>, <c>Func&lt;T&gt; MakeGetter&lt;T&gt;(T)</c>),
    /// a non-generic source body that returns the parameter, or a non-generic
    /// storage-returning delegate. Returns <see langword="null"/> when the
    /// argument is not a method group (Codex P1 on #3441).
    /// </summary>
    private static bool? MethodGroupSelectorForwardsElement(
        ExpressionSyntax expression,
        bool considerLastParameter,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (expression is not (SimpleNameSyntax or MemberAccessExpressionSyntax))
            return null;

        var symbolInfo = semanticModel.GetSymbolInfo(expression, cancellationToken);
        var group = symbolInfo.Symbol as IMethodSymbol;
        if (group == null && symbolInfo.CandidateSymbols.Length == 1)
            group = symbolInfo.CandidateSymbols[0] as IMethodSymbol;

        if (group == null)
            return null;

        var original = (group.ReducedFrom ?? group).OriginalDefinition;
        var extensionOffset = group.ReducedFrom != null ? 1 : 0;
        if (original.Parameters.Length <= extensionOffset || original.ReturnsVoid)
            return false;

        var candidates = new List<int> { extensionOffset };
        if (considerLastParameter && original.Parameters.Length - 1 > extensionOffset)
            candidates.Add(original.Parameters.Length - 1);

        foreach (var ordinal in candidates)
        {
            var elementParameter = original.Parameters[ordinal];
            var typeParameters = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
            CollectMethodTypeParameters(elementParameter.Type, typeParameters);
            if (typeParameters.Count > 0 &&
                TypeContainsAnyMethodTypeParameter(original.ReturnType, typeParameters))
            {
                return true;
            }

            if (IsStorageReturningDelegateType(original.ReturnType, elementParameter.Type))
                return true;

            foreach (var reference in original.DeclaringSyntaxReferences)
            {
                var (expressionBody, block) = reference.GetSyntax(cancellationToken) switch
                {
                    MethodDeclarationSyntax declaration => (declaration.ExpressionBody?.Expression, declaration.Body),
                    LocalFunctionStatementSyntax local => (local.ExpressionBody?.Expression, local.Body),
                    _ => ((ExpressionSyntax?)null, (BlockSyntax?)null)
                };

                if ((expressionBody != null || block != null) &&
                    IsIdentityLambdaBody(expressionBody, block, elementParameter.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True for <c>Select</c>/<c>SelectMany</c>/<c>ConvertAll</c> whose selector returns
    /// a delegate that itself yields the element (e.g. <c>x =&gt; () =&gt; x</c>), so
    /// <c>points.Select(x =&gt; () =&gt; x).ToArray(); getters[0]()</c> still aliases
    /// (Codex P1 on #3441).
    /// </summary>
    private static bool IsStorageReturningDelegateSelectorInvocation(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var name = (method.ReducedFrom ?? method).OriginalDefinition.Name;
        if (name is not ("Select" or "SelectMany" or "ConvertAll"))
            return false;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var expr = SkipTransparentWrappers(argument.Expression);

            if (expr is SimpleLambdaExpressionSyntax simple &&
                LambdaReturnsStorageReturningDelegate(
                    simple.ExpressionBody,
                    simple.Block,
                    simple.Parameter.Identifier.ValueText,
                    semanticModel,
                    cancellationToken))
            {
                return true;
            }

            if (expr is ParenthesizedLambdaExpressionSyntax paren &&
                paren.ParameterList.Parameters.Count >= 1)
            {
                var first = paren.ParameterList.Parameters[0].Identifier.ValueText;
                if (LambdaReturnsStorageReturningDelegate(
                        paren.ExpressionBody,
                        paren.Block,
                        first,
                        semanticModel,
                        cancellationToken))
                {
                    return true;
                }

                // SelectMany result selector may forward the inner element.
                if (name == "SelectMany" &&
                    paren.ParameterList.Parameters.Count > 1)
                {
                    var last = paren.ParameterList.Parameters[^1].Identifier.ValueText;
                    if (LambdaReturnsStorageReturningDelegate(
                            paren.ExpressionBody,
                            paren.Block,
                            last,
                            semanticModel,
                            cancellationToken))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when a selector body yields a storage-returning delegate that forwards
    /// <paramref name="parameterName"/> — either an inline anonymous function
    /// (<c>x =&gt; () =&gt; x</c>) or an opaque call whose return is such a delegate
    /// (<c>x =&gt; MakeGetter(x)</c>) (Codex P1 on #3441).
    /// </summary>
    private static bool LambdaReturnsStorageReturningDelegate(
        ExpressionSyntax? expressionBody,
        BlockSyntax? block,
        string parameterName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var returned in EnumerateSelectorReturnExpressions(expressionBody, block))
        {
            foreach (var leaf in EnumerateForwardedValueLeaves(returned))
            {
                if (leaf is InvocationExpressionSyntax invocation &&
                    InvocationReturnsStorageReturningDelegateCarrier(
                        invocation, parameterName, semanticModel, cancellationToken))
                {
                    return true;
                }

                if (leaf is not AnonymousFunctionExpressionSyntax nested)
                    continue;

                if (IsIdentityLambdaBody(
                        nested.ExpressionBody, nested.Block, parameterName))
                {
                    return true;
                }

                // x => () => () => x — nested storage-returning delegate carriers.
                if (LambdaReturnsStorageReturningDelegate(
                        nested.ExpressionBody,
                        nested.Block,
                        parameterName,
                        semanticModel,
                        cancellationToken))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="invocation"/> receives <paramref name="parameterName"/>
    /// (as an argument or extension/instance receiver) and its return type is a
    /// (possibly nested) delegate that yields that parameter's storage type —
    /// e.g. <c>MakeGetter(x)</c> with <c>Func&lt;T&gt; MakeGetter&lt;T&gt;(T value)</c>
    /// (Codex P1 on #3441).
    /// </summary>
    private static bool InvocationReturnsStorageReturningDelegateCarrier(
        InvocationExpressionSyntax invocation,
        string parameterName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ITypeSymbol? storageType = null;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (TryGetNamedStorageType(
                    argument.Expression, parameterName, semanticModel, cancellationToken, out storageType))
            {
                break;
            }
        }

        if (storageType == null &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            TryGetNamedStorageType(
                memberAccess.Expression, parameterName, semanticModel, cancellationToken, out storageType);
        }

        if (storageType == null)
            return false;

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
            method.ReturnsVoid)
        {
            return false;
        }

        return IsStorageReturningDelegateType(method.ReturnType, storageType);
    }

    private static bool TryGetNamedStorageType(
        ExpressionSyntax expression,
        string parameterName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ITypeSymbol? storageType)
    {
        storageType = null;
        foreach (var leaf in EnumerateForwardedValueLeaves(expression))
        {
            if (leaf is IdentifierNameSyntax id &&
                id.Identifier.ValueText == parameterName)
            {
                storageType = semanticModel.GetTypeInfo(leaf, cancellationToken).Type;
                return storageType != null;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> is a delegate (possibly nested
    /// <c>Func&lt;Func&lt;T&gt;&gt;</c>) whose ultimate invoke return embeds
    /// <paramref name="storageType"/>.
    /// </summary>
    private static bool IsStorageReturningDelegateType(ITypeSymbol? type, ITypeSymbol storageType)
    {
        var current = type;
        while (current is INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
        {
            if (invoke.ReturnsVoid)
                return false;

            var returnType = invoke.ReturnType;
            if (TypeContainsOrEquals(returnType, storageType))
                return true;

            // Nested storage-returning delegate: Func<Func<T>>.
            if (returnType is INamedTypeSymbol { DelegateInvokeMethod: not null })
            {
                current = returnType;
                continue;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="method"/> returns a (possibly nested) delegate
    /// that yields <paramref name="parameter"/>'s type — covering opaque
    /// <c>MakeGetter(point)</c> carriers, including non-generic signatures
    /// (Codex P1 on #3441).
    /// </summary>
    private static bool MethodReturnWrapsElementAsStorageReturningDelegate(
        IMethodSymbol method,
        IParameterSymbol parameter) =>
        IsStorageReturningDelegateType(method.ReturnType, parameter.Type);

    private static bool IsIdentityLambdaBody(
        ExpressionSyntax? expressionBody,
        BlockSyntax? block,
        string parameterName)
    {
        // Any returned value path that yields the element parameter forwards
        // storage: x => x, x => flag ? x : x, x => (T)x, x => k switch { _ => x },
        // x => { if (flag) return x; return x; } (Codex P1 on #3441).
        foreach (var returned in EnumerateSelectorReturnExpressions(expressionBody, block))
        {
            foreach (var leaf in EnumerateForwardedValueLeaves(returned))
            {
                if (leaf is IdentifierNameSyntax id &&
                    id.Identifier.ValueText == parameterName)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returned expressions of a selector body, excluding returns that belong to
    /// nested lambdas / local functions.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateSelectorReturnExpressions(
        ExpressionSyntax? expressionBody,
        BlockSyntax? block) =>
        EnumerateFunctionReturnExpressions(expressionBody, block);

    /// <summary>
    /// Value leaves an expression may evaluate to: unwraps parentheses, casts,
    /// null-forgiving, <c>with</c> receivers, assignment-as-value RHS, <c>?:</c>
    /// branches, <c>??</c> operands and switch-expression arms.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateForwardedValueLeaves(ExpressionSyntax expression)
    {
        var pending = new Stack<ExpressionSyntax>();
        pending.Push(expression);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    pending.Push(parenthesized.Expression);
                    break;
                case CastExpressionSyntax cast:
                    pending.Push(cast.Expression);
                    break;
                case PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    pending.Push(postfix.Operand);
                    break;
                case CheckedExpressionSyntax checkedExpression:
                    pending.Push(checkedExpression.Expression);
                    break;
                case ConditionalExpressionSyntax conditional:
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.WhenFalse);
                    break;
                case BinaryExpressionSyntax coalesce when
                    coalesce.IsKind(SyntaxKind.CoalesceExpression):
                    pending.Push(coalesce.Left);
                    pending.Push(coalesce.Right);
                    break;
                case SwitchExpressionSyntax switchExpression:
                    foreach (var arm in switchExpression.Arms)
                        pending.Push(arm.Expression);
                    break;
                case RefExpressionSyntax refExpression:
                    pending.Push(refExpression.Expression);
                    break;
                case WithExpressionSyntax withExpression:
                    pending.Push(withExpression.Expression);
                    break;
                case AssignmentExpressionSyntax assignment when
                    assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                    pending.Push(assignment.Right);
                    break;
                default:
                    yield return current;
                    break;
            }
        }
    }

    /// <summary>
    /// True when an anonymous function / lambda returns converted storage
    /// (<c>() =&gt; point</c> / <c>() =&gt; { return point; }</c>) so the assigned
    /// delegate becomes a tracked carrier (Codex P1 on #3441).
    /// </summary>
    private static bool AnonymousFunctionReturnsConvertedStorage(
        AnonymousFunctionExpressionSyntax anonymousFunction,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var returned in EnumerateFunctionReturnExpressions(
                     anonymousFunction.ExpressionBody, anonymousFunction.Block))
        {
            if (ExpressionReferencesStorageAlias(
                    returned, aliases, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a local function returns converted storage
    /// (<c>(int X, int Y) Get() =&gt; point;</c>) so invocations alias (Codex P1 on #3441).
    /// </summary>
    private static bool LocalFunctionReturnsConvertedStorage(
        LocalFunctionStatementSyntax localFunction,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var returned in EnumerateFunctionReturnExpressions(
                     localFunction.ExpressionBody?.Expression,
                     localFunction.Body))
        {
            if (ExpressionReferencesStorageAlias(
                    returned, aliases, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ExpressionSyntax> EnumerateFunctionReturnExpressions(
        ExpressionSyntax? expressionBody,
        BlockSyntax? block)
    {
        if (expressionBody != null)
            yield return expressionBody;

        if (block == null)
            yield break;

        // Skip nested lambdas / local functions so their returns are not
        // attributed to the outer function (Codex P2 on #3441).
        foreach (var statement in block
                     .DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                     .OfType<ReturnStatementSyntax>())
        {
            if (statement.Expression != null)
                yield return statement.Expression;
        }
    }

    private static ITypeSymbol? TryGetSequenceElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return array.ElementType;

        if (type is INamedTypeSymbol named)
        {
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                named.TypeArguments.Length == 1)
            {
                return named.TypeArguments[0];
            }

            // Span<T> / ReadOnlySpan<T> / Memory<T> / ReadOnlyMemory<T>
            if (named.TypeArguments.Length == 1 &&
                named.ContainingNamespace?.ToDisplayString() == "System" &&
                named.Name is "Span" or "ReadOnlySpan" or "Memory" or "ReadOnlyMemory")
            {
                return named.TypeArguments[0];
            }

            foreach (var iface in named.AllInterfaces)
            {
                if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                    iface.TypeArguments.Length == 1)
                {
                    return iface.TypeArguments[0];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="expression"/> yields the converted tuple value —
    /// directly (storage alias / container element of that type) or through one or
    /// more aggregate carrier members (tuple or anonymous-object properties that
    /// embed converted storage, including nested carriers).
    /// </summary>
    private static bool ExpressionYieldsConvertedStorage(
        ExpressionSyntax expression,
        HashSet<ISymbol> storages,
        ITypeSymbol? tupleType,
        SemanticModel semanticModel,
        SyntaxNode root)
    {
        expression = UnwrapParenthesesAndNullForgiving(expression);

        // A nested convertible creation of the same shape IS the converted storage
        // itself — needed when an opaque constructor forwards an inline literal
        // (new Holder((X: 1, Y: 2)) → holder.Value) rather than a tracked local
        // (Codex P1 on #3441, PRRT_kwDORD0nYs603sqr).
        if (tupleType != null &&
            IsRewritableCreationSyntax(expression) &&
            SymbolEqualityComparer.Default.Equals(
                semanticModel.GetTypeInfo(expression).Type, tupleType))
        {
            return true;
        }

        if (ReceiverYieldsConvertedStorage(expression, storages, tupleType, semanticModel))
            return true;

        if (expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        // Try every candidate carrier initializer (constant-index path plus
        // conservative nonconstant-index enumeration — Codex P1).
        foreach (var initializer in EnumerateCarriedMemberInitializers(
                     memberAccess, storages, semanticModel, root))
        {
            if (ExpressionYieldsConvertedStorage(
                    initializer, storages, tupleType, semanticModel, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the initializer expression assigned to <paramref name="memberAccess"/>'s
    /// member on a tracked aggregate (or nested carrier), when that aggregate was
    /// created with a tuple or anonymous-object literal.
    /// </summary>
    private static bool TryGetCarriedMemberInitializer(
        MemberAccessExpressionSyntax memberAccess,
        HashSet<ISymbol> storages,
        SemanticModel semanticModel,
        SyntaxNode root,
        out ExpressionSyntax? initializer)
    {
        initializer = null;
        foreach (var candidate in EnumerateCarriedMemberInitializers(
                     memberAccess, storages, semanticModel, root))
        {
            initializer = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Yields every initializer that <paramref name="memberAccess"/>'s member may
    /// resolve to on a tracked aggregate, including every element when the
    /// receiver uses a nonconstant container index.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateCarriedMemberInitializers(
        MemberAccessExpressionSyntax memberAccess,
        HashSet<ISymbol> storages,
        SemanticModel semanticModel,
        SyntaxNode root)
    {
        var memberName = memberAccess.Name.Identifier.ValueText;

        foreach (var creation in EnumerateAggregateCreations(
                     memberAccess.Expression, storages, semanticModel, root))
        {
            if (TryGetNamedMemberInitializer(
                    creation, memberName, semanticModel, out var initializer) &&
                initializer != null)
            {
                yield return initializer;
            }

            // Pair(point)-style multi-arg / tuple generic returns have no literal
            // initializer to walk; map return members typed as a parameter back to
            // the corresponding arguments (Codex P1 on #3441).
            foreach (var forwarded in EnumerateInvocationForwardedMemberArguments(
                         creation, memberName, semanticModel))
            {
                yield return forwarded;
            }

            // Ordinary constructors that capture into object/dynamic/generic fields
            // (new Holder(point) → holder.Value) — Codex P1 on #3441.
            foreach (var forwarded in EnumerateObjectCreationForwardedConstructorArguments(
                         creation, memberName, semanticModel))
            {
                yield return forwarded;
            }
        }
    }

    private static bool TryResolveAggregateCreation(
        ExpressionSyntax receiver,
        HashSet<ISymbol> storages,
        SemanticModel semanticModel,
        SyntaxNode root,
        out ExpressionSyntax? creation)
    {
        creation = null;
        foreach (var candidate in EnumerateAggregateCreations(
                     receiver, storages, semanticModel, root))
        {
            creation = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Yields aggregate creation expressions reachable from
    /// <paramref name="receiver"/> (tracked symbol initializer, nested carrier
    /// member, or container element — including every element when the index is
    /// nonconstant).
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateAggregateCreations(
        ExpressionSyntax receiver,
        HashSet<ISymbol> storages,
        SemanticModel semanticModel,
        SyntaxNode root)
    {
        receiver = UnwrapParenthesesAndNullForgiving(receiver);

        var symbol = semanticModel.GetSymbolInfo(receiver).Symbol;
        if (symbol != null && storages.Contains(symbol))
        {
            if (TryFindSymbolInitializerExpression(
                    symbol, root, semanticModel, out var creation) &&
                creation != null)
            {
                yield return creation;
                yield break;
            }

            // foreach / from / join / let variables have no declarator initializer;
            // recover the projections that supply them, e.g.
            // foreach (var box in from n in xs select new { Value = point }) (Codex P1).
            foreach (var origin in EnumerateIterationVariableOrigins(
                         symbol, semanticModel, root, depth: 0))
            {
                yield return origin;
            }

            yield break;
        }

        // Nested carrier: outer.Inner where Inner's initializer is itself an aggregate.
        if (receiver is MemberAccessExpressionSyntax nested)
        {
            foreach (var initializer in EnumerateCarriedMemberInitializers(
                         nested, storages, semanticModel, root))
            {
                yield return initializer;
            }

            yield break;
        }

        // Container element carrier: boxes[0] / boxes[i] where
        // boxes = new[] { new { Value = point } } (Codex P1 — peel element-access;
        // nonconstant indices enumerate every initialized element).
        if (receiver is ElementAccessExpressionSyntax elementAccess)
        {
            if (!TryResolveContainerInitializer(
                    elementAccess.Expression,
                    storages,
                    semanticModel,
                    root,
                    out var containerInitializer) ||
                containerInitializer == null)
            {
                yield break;
            }

            if (TryGetConstantElementIndex(elementAccess, semanticModel, out var index))
            {
                if (TryGetElementAtConstantIndex(
                        containerInitializer, index, out var element) &&
                    element != null)
                {
                    yield return element;
                }

                yield break;
            }

            foreach (var element in EnumerateContainerElementExpressions(containerInitializer))
                yield return element;
        }
    }

    private const int MaxCarrierOriginDepth = 8;

    /// <summary>
    /// Yields the per-element expressions that supply an iteration / range
    /// variable without a declarator initializer: <c>foreach</c> variables,
    /// <c>from</c> / <c>join</c> range variables (elements of their source
    /// sequence) and <c>let</c> variables (the let expression).
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateIterationVariableOrigins(
        ISymbol symbol,
        SemanticModel semanticModel,
        SyntaxNode root,
        int depth)
    {
        if (depth > MaxCarrierOriginDepth)
            yield break;

        foreach (var node in root.DescendantNodes())
        {
            ExpressionSyntax? sequence = null;
            ExpressionSyntax? direct = null;
            switch (node)
            {
                case ForEachStatementSyntax forEach when
                    SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetDeclaredSymbol(forEach), symbol):
                    sequence = forEach.Expression;
                    break;
                case FromClauseSyntax fromClause when
                    SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetDeclaredSymbol(fromClause), symbol):
                    sequence = fromClause.Expression;
                    break;
                case JoinClauseSyntax joinClause when
                    SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetDeclaredSymbol(joinClause), symbol):
                    sequence = joinClause.InExpression;
                    break;
                case LetClauseSyntax letClause when
                    SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetDeclaredSymbol(letClause), symbol):
                    direct = letClause.Expression;
                    break;
                default:
                    continue;
            }

            if (direct != null)
            {
                foreach (var origin in EnumerateCarrierOrigins(direct, semanticModel, root, depth + 1))
                    yield return origin;
            }
            else if (sequence != null)
            {
                foreach (var element in EnumerateSequenceElementOrigins(
                             sequence, semanticModel, root, depth + 1))
                {
                    yield return element;
                }
            }

            yield break;
        }
    }

    /// <summary>
    /// Yields element expressions produced by <paramref name="sequence"/>: the
    /// final <c>select</c> projection of a query, the elements of an array /
    /// collection literal, or (through a local initializer) either of those.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateSequenceElementOrigins(
        ExpressionSyntax sequence,
        SemanticModel semanticModel,
        SyntaxNode root,
        int depth)
    {
        if (depth > MaxCarrierOriginDepth)
            yield break;

        sequence = UnwrapParenthesesAndNullForgiving(sequence);

        if (sequence is QueryExpressionSyntax query)
        {
            var body = query.Body;
            while (body.Continuation != null)
                body = body.Continuation.Body;

            if (body.SelectOrGroup is SelectClauseSyntax select)
            {
                foreach (var origin in EnumerateCarrierOrigins(
                             select.Expression, semanticModel, root, depth + 1))
                {
                    yield return origin;
                }
            }

            yield break;
        }

        var yieldedElement = false;
        foreach (var element in EnumerateContainerElementExpressions(sequence))
        {
            yieldedElement = true;
            foreach (var origin in EnumerateCarrierOrigins(element, semanticModel, root, depth + 1))
                yield return origin;
        }

        if (yieldedElement)
            yield break;

        var symbol = semanticModel.GetSymbolInfo(sequence).Symbol;
        if (symbol != null &&
            TryFindSymbolInitializerExpression(symbol, root, semanticModel, out var initializer) &&
            initializer != null)
        {
            foreach (var element in EnumerateSequenceElementOrigins(
                         initializer, semanticModel, root, depth + 1))
            {
                yield return element;
            }
        }
    }

    /// <summary>
    /// Yields <paramref name="expression"/> itself plus, when it names a local or
    /// range variable, the expressions that originally supplied that variable
    /// (declarator initializer or iteration / query source).
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateCarrierOrigins(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        SyntaxNode root,
        int depth)
    {
        expression = UnwrapParenthesesAndNullForgiving(expression);
        yield return expression;

        if (depth > MaxCarrierOriginDepth || expression is not IdentifierNameSyntax)
            yield break;

        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        if (symbol is not (ILocalSymbol or IRangeVariableSymbol))
            yield break;

        if (TryFindSymbolInitializerExpression(symbol, root, semanticModel, out var initializer) &&
            initializer != null)
        {
            foreach (var origin in EnumerateCarrierOrigins(initializer, semanticModel, root, depth + 1))
                yield return origin;
            yield break;
        }

        foreach (var origin in EnumerateIterationVariableOrigins(symbol, semanticModel, root, depth + 1))
            yield return origin;
    }

    /// <summary>
    /// Resolves the initializer of a tracked container element
    /// (<c>boxes[0]</c> → <c>new { Value = point }</c>) so aggregate carrier
    /// lookup can continue through the element. Constant indices only; nonconstant
    /// indices are enumerated by <see cref="EnumerateAggregateCreations"/>.
    /// </summary>
    private static bool TryResolveContainerElementInitializer(
        ElementAccessExpressionSyntax elementAccess,
        HashSet<ISymbol> storages,
        SemanticModel semanticModel,
        SyntaxNode root,
        out ExpressionSyntax? elementInitializer)
    {
        elementInitializer = null;

        if (!TryResolveContainerInitializer(
                elementAccess.Expression, storages, semanticModel, root, out var containerInitializer) ||
            containerInitializer == null)
        {
            return false;
        }

        return TryGetConstantIndexedElement(
            containerInitializer, elementAccess, semanticModel, out elementInitializer);
    }

    /// <summary>
    /// Resolves the expression that initialized a container receiver (tracked
    /// symbol, nested carrier member, or another indexed container).
    /// </summary>
    private static bool TryResolveContainerInitializer(
        ExpressionSyntax container,
        HashSet<ISymbol> storages,
        SemanticModel semanticModel,
        SyntaxNode root,
        out ExpressionSyntax? initializer)
    {
        initializer = null;
        container = UnwrapParenthesesAndNullForgiving(container);

        var symbol = semanticModel.GetSymbolInfo(container).Symbol;
        if (symbol != null && storages.Contains(symbol))
            return TryFindSymbolInitializerExpression(symbol, root, semanticModel, out initializer);

        if (container is MemberAccessExpressionSyntax nested &&
            TryGetCarriedMemberInitializer(nested, storages, semanticModel, root, out initializer))
        {
            return initializer != null;
        }

        if (container is ElementAccessExpressionSyntax nestedElement &&
            TryResolveContainerElementInitializer(
                nestedElement, storages, semanticModel, root, out initializer))
        {
            return initializer != null;
        }

        return false;
    }

    /// <summary>
    /// Picks the element expression at a constant index from an array /
    /// collection creation initializer.
    /// </summary>
    private static bool TryGetConstantIndexedElement(
        ExpressionSyntax containerInitializer,
        ElementAccessExpressionSyntax elementAccess,
        SemanticModel semanticModel,
        out ExpressionSyntax? element)
    {
        element = null;
        if (!TryGetConstantElementIndex(elementAccess, semanticModel, out var index))
            return false;

        return TryGetElementAtConstantIndex(containerInitializer, index, out element);
    }

    /// <summary>
    /// Picks the element expression at <paramref name="index"/> from an array /
    /// collection creation initializer.
    /// </summary>
    private static bool TryGetElementAtConstantIndex(
        ExpressionSyntax containerInitializer,
        int index,
        out ExpressionSyntax? element)
    {
        element = null;
        containerInitializer = UnwrapParenthesesAndNullForgiving(containerInitializer);

        SeparatedSyntaxList<ExpressionSyntax>? expressions = containerInitializer switch
        {
            ImplicitArrayCreationExpressionSyntax implicitArray
                when implicitArray.Initializer != null => implicitArray.Initializer.Expressions,
            ArrayCreationExpressionSyntax arrayCreation
                when arrayCreation.Initializer != null => arrayCreation.Initializer.Expressions,
            ImplicitStackAllocArrayCreationExpressionSyntax implicitStackAlloc
                when implicitStackAlloc.Initializer != null => implicitStackAlloc.Initializer.Expressions,
            StackAllocArrayCreationExpressionSyntax stackAlloc
                when stackAlloc.Initializer != null => stackAlloc.Initializer.Expressions,
            // object[] values = { point, new Other() }; — bare array initializer.
            InitializerExpressionSyntax arrayInitializer
                when arrayInitializer.IsKind(SyntaxKind.ArrayInitializerExpression)
                => arrayInitializer.Expressions,
            _ => null
        };

        if (expressions != null)
        {
            if (index < 0 || index >= expressions.Value.Count)
                return false;

            element = expressions.Value[index];
            return true;
        }

        if (containerInitializer is CollectionExpressionSyntax collection)
        {
            var seen = 0;
            foreach (var collectionElement in collection.Elements)
            {
                if (collectionElement is not ExpressionElementSyntax expressionElement)
                    continue;

                if (seen == index)
                {
                    element = expressionElement.Expression;
                    return true;
                }

                seen++;
            }

            return false;
        }

        // new List<T> { expr0, expr1 } / similar object-creation collection initializers.
        if (containerInitializer is BaseObjectCreationExpressionSyntax objectCreation &&
            objectCreation.Initializer != null)
        {
            var initExpressions = objectCreation.Initializer.Expressions;
            if (index < 0 || index >= initExpressions.Count)
                return false;

            element = initExpressions[index];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Enumerates element expressions from an array / collection creation
    /// initializer (used for conservative nonconstant-index carrier resolution).
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateContainerElementExpressions(
        ExpressionSyntax containerInitializer)
    {
        containerInitializer = UnwrapParenthesesAndNullForgiving(containerInitializer);

        SeparatedSyntaxList<ExpressionSyntax>? expressions = containerInitializer switch
        {
            ImplicitArrayCreationExpressionSyntax implicitArray
                when implicitArray.Initializer != null => implicitArray.Initializer.Expressions,
            ArrayCreationExpressionSyntax arrayCreation
                when arrayCreation.Initializer != null => arrayCreation.Initializer.Expressions,
            ImplicitStackAllocArrayCreationExpressionSyntax implicitStackAlloc
                when implicitStackAlloc.Initializer != null => implicitStackAlloc.Initializer.Expressions,
            StackAllocArrayCreationExpressionSyntax stackAlloc
                when stackAlloc.Initializer != null => stackAlloc.Initializer.Expressions,
            InitializerExpressionSyntax arrayInitializer
                when arrayInitializer.IsKind(SyntaxKind.ArrayInitializerExpression)
                => arrayInitializer.Expressions,
            _ => null
        };

        if (expressions != null)
        {
            foreach (var expression in expressions.Value)
                yield return expression;
            yield break;
        }

        if (containerInitializer is CollectionExpressionSyntax collection)
        {
            foreach (var collectionElement in collection.Elements)
            {
                if (collectionElement is ExpressionElementSyntax expressionElement)
                    yield return expressionElement.Expression;
                else if (collectionElement is SpreadElementSyntax spreadElement)
                    yield return spreadElement.Expression;
            }

            yield break;
        }

        if (containerInitializer is BaseObjectCreationExpressionSyntax objectCreation &&
            objectCreation.Initializer != null)
        {
            foreach (var expression in objectCreation.Initializer.Expressions)
                yield return expression;
        }
    }

    private static bool TryGetConstantElementIndex(
        ElementAccessExpressionSyntax elementAccess,
        SemanticModel semanticModel,
        out int index)
    {
        index = -1;
        if (elementAccess.ArgumentList.Arguments.Count != 1)
            return false;

        var arg = elementAccess.ArgumentList.Arguments[0].Expression;
        var constant = semanticModel.GetConstantValue(arg);
        if (!constant.HasValue)
            return false;

        index = constant.Value switch
        {
            int i => i,
            long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
            uint ui when ui <= int.MaxValue => (int)ui,
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            char c => c,
            _ => -1
        };

        return index >= 0;
    }

    private static bool TryFindSymbolInitializerExpression(
        ISymbol symbol,
        SyntaxNode root,
        SemanticModel semanticModel,
        out ExpressionSyntax? initializer)
    {
        initializer = null;
        foreach (var node in root.DescendantNodes())
        {
            if (node is VariableDeclaratorSyntax declarator &&
                SymbolEqualityComparer.Default.Equals(
                    semanticModel.GetDeclaredSymbol(declarator),
                    symbol) &&
                declarator.Initializer?.Value is { } value)
            {
                initializer = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an object creation's constructor receives converted storage into
    /// an <c>object</c> / <c>dynamic</c> / type-parameter parameter (or a parameter
    /// whose constructed type already embeds tracked storage) — e.g.
    /// <c>new Holder(point)</c> with <c>Holder(object value)</c> (Codex P1 on #3441).
    /// </summary>
    private static bool ObjectCreationCapturesConvertedStorageViaConstructor(
        BaseObjectCreationExpressionSyntax objectCreation,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (objectCreation.ArgumentList == null ||
            objectCreation.ArgumentList.Arguments.Count == 0)
        {
            return false;
        }

        if (semanticModel.GetSymbolInfo(objectCreation, cancellationToken).Symbol is not IMethodSymbol constructor)
            return false;

        for (var i = 0; i < objectCreation.ArgumentList.Arguments.Count; i++)
        {
            var argument = objectCreation.ArgumentList.Arguments[i];
            if (!ExpressionReferencesStorageAlias(
                    argument.Expression, aliases, semanticModel, cancellationToken))
            {
                continue;
            }

            var parameter = ResolveInvocationParameter(constructor, argument, i);
            if (parameter == null)
                continue;

            if (ParameterTypeMayCaptureConvertedStorage(parameter.Type) ||
                TypeInvolvesStorageAliasType(parameter.Type, aliases))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a constructor / factory parameter type can hide converted storage
    /// without preserving the tuple shape: <c>object</c>, <c>dynamic</c>, or a
    /// method/type type parameter (Codex P1 on #3441).
    /// </summary>
    private static bool ParameterTypeMayCaptureConvertedStorage(ITypeSymbol parameterType) =>
        parameterType.SpecialType == SpecialType.System_Object ||
        parameterType.TypeKind == TypeKind.Dynamic ||
        parameterType is ITypeParameterSymbol;

    /// <summary>
    /// For <c>new Holder(point)</c> with <c>Holder(object value)</c> storing into
    /// <c>dynamic Value</c>, yields constructor arguments that may surface through
    /// an opaque object/dynamic/generic member read (Codex P1 on #3441).
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateObjectCreationForwardedConstructorArguments(
        ExpressionSyntax creation,
        string memberName,
        SemanticModel semanticModel)
    {
        creation = UnwrapParenthesesAndNullForgiving(creation);
        if (creation is not BaseObjectCreationExpressionSyntax { ArgumentList: { } argumentList } objectCreation ||
            argumentList.Arguments.Count == 0)
        {
            yield break;
        }

        if (semanticModel.GetSymbolInfo(objectCreation).Symbol is not IMethodSymbol constructor)
            yield break;

        var constructedType = semanticModel.GetTypeInfo(objectCreation).Type as INamedTypeSymbol;
        if (constructedType == null)
            yield break;

        ISymbol? member = constructedType.GetMembers(memberName)
            .FirstOrDefault(candidate => candidate is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false });
        var memberType = member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null
        };
        if (memberType == null || !MemberTypeMayHoldOpaqueConstructorCapture(memberType))
            yield break;

        for (var i = 0; i < argumentList.Arguments.Count; i++)
        {
            var argument = argumentList.Arguments[i];
            var parameter = ResolveInvocationParameter(constructor, argument, i);
            if (parameter == null)
                continue;

            if (!ParameterTypeMayCaptureConvertedStorage(parameter.Type))
                continue;

            yield return argument.Expression;
        }
    }

    /// <summary>
    /// Members that can surface an opaque constructor capture without preserving
    /// the original tuple type: <c>object</c>, <c>dynamic</c>, or a type parameter.
    /// </summary>
    private static bool MemberTypeMayHoldOpaqueConstructorCapture(ITypeSymbol memberType) =>
        memberType.SpecialType == SpecialType.System_Object ||
        memberType.TypeKind == TypeKind.Dynamic ||
        memberType is ITypeParameterSymbol;

    /// <summary>
    /// True when <paramref name="element"/> may be a nested convertible tuple
    /// creation whose enclosing container is the tracked storage (Codex nested
    /// creation / enclosing-storage path).
    /// </summary>
    private static bool ElementMayBeNestedConvertedCreation(ExpressionSyntax element)
    {
        element = UnwrapParenthesesAndNullForgiving(element);
        while (TryUnwrapTransparentWrapper(element, out var inner))
            element = UnwrapParenthesesAndNullForgiving(inner);

        return element is TupleExpressionSyntax;
    }

    /// <summary>
    /// When <paramref name="elementAccess"/> uses a constant index into a tracked
    /// container whose initializer is known, resolves the specific element
    /// expression (Codex P2 on #3441).
    /// </summary>
    private static bool TryResolveConstantIndexedContainerElement(
        ElementAccessExpressionSyntax elementAccess,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ExpressionSyntax? element)
    {
        element = null;
        if (!TryGetConstantElementIndex(elementAccess, semanticModel, out var index))
            return false;

        // Dictionary / custom indexers use keys, not positional initializer indices —
        // only arrays get precise constant-index resolution (Codex P2 on #3441).
        var containerType = semanticModel.GetTypeInfo(elementAccess.Expression, cancellationToken).Type;
        if (containerType is not IArrayTypeSymbol)
            return false;

        var container = UnwrapParenthesesAndNullForgiving(elementAccess.Expression);
        ExpressionSyntax? containerInitializer = null;

        // Direct creation / initializer expression as the indexed receiver.
        if (container is ImplicitArrayCreationExpressionSyntax or ArrayCreationExpressionSyntax
            or CollectionExpressionSyntax or BaseObjectCreationExpressionSyntax
            or InitializerExpressionSyntax)
        {
            containerInitializer = container;
        }
        else
        {
            var containerSymbol = semanticModel.GetSymbolInfo(container, cancellationToken).Symbol;
            if (containerSymbol == null || !aliases.Contains(containerSymbol))
                return false;

            if (!TryFindSymbolInitializerViaDeclaringReferences(containerSymbol, out containerInitializer) ||
                containerInitializer == null)
            {
                return false;
            }
        }

        return TryGetElementAtConstantIndex(containerInitializer, index, out element) &&
               element != null;
    }

    /// <summary>
    /// Locates a symbol's initializer expression via declaring syntax references
    /// (no syntax-tree root walk required).
    /// </summary>
    private static bool TryFindSymbolInitializerViaDeclaringReferences(
        ISymbol symbol,
        out ExpressionSyntax? initializer)
    {
        initializer = null;
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax declarator &&
                declarator.Initializer?.Value is { } value)
            {
                initializer = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For an invocation creation such as <c>Pair(point)</c> returning
    /// <c>(T Value, int Other)</c>, yields argument expressions whose parameter
    /// type matches the named return member's type so carrier lookup can treat
    /// <c>wrapped.Value</c> as forwarding <c>point</c> (Codex P1 on #3441).
    /// </summary>
    private static IEnumerable<ExpressionSyntax> EnumerateInvocationForwardedMemberArguments(
        ExpressionSyntax creation,
        string memberName,
        SemanticModel semanticModel)
    {
        creation = UnwrapParenthesesAndNullForgiving(creation);
        if (creation is not InvocationExpressionSyntax invocation)
            yield break;

        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            yield break;

        if (!TryGetReturnMemberType(method.ReturnType, memberName, out var memberType) ||
            memberType == null)
        {
            yield break;
        }

        var args = invocation.ArgumentList.Arguments;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var parameter = ResolveInvocationParameter(method, arg, i);
            if (parameter == null)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(parameter.Type, memberType) &&
                !TypeContainsOrEquals(memberType, parameter.Type) &&
                !TypeContainsOrEquals(parameter.Type, memberType))
            {
                continue;
            }

            // Generic wrappers (Box<T> Wrap<T>(T value)): constructed-type equality
            // also matches fixed members such as Box<T>.Other declared as
            // (int X, int Y). A method-type-parameter argument can only reach
            // members whose original declared type mentions the containing-type
            // parameter bound to it (Codex P2 on #3441, PRRT_kwDORD0nYs600gme).
            if (!GenericParameterMayReachWrapperMember(method, parameter, memberName))
                continue;

            yield return arg.Expression;
        }
    }

    /// <summary>
    /// False only when <paramref name="parameter"/>'s original type is built from
    /// method type parameters and the named field/property on the original return
    /// type is declared without any containing-type parameter bound to them.
    /// Refusal-leaning: concrete parameter types, tuple returns, and unresolved
    /// shapes return true.
    /// </summary>
    private static bool GenericParameterMayReachWrapperMember(
        IMethodSymbol method,
        IParameterSymbol parameter,
        string memberName)
    {
        var original = (method.ReducedFrom ?? method).OriginalDefinition;
        var ordinal = parameter.Ordinal + (method.ReducedFrom != null ? 1 : 0);
        if (ordinal < 0 || ordinal >= original.Parameters.Length)
            return true;

        var methodTypeParameters = original.TypeParameters
            .Where(typeParameter => TypeContainsOrEquals(original.Parameters[ordinal].Type, typeParameter))
            .ToList();
        if (methodTypeParameters.Count == 0)
            return true;

        if (original.ReturnType is not INamedTypeSymbol { IsTupleType: false, IsAnonymousType: false } returnType)
            return true;

        ISymbol? member = returnType.GetMembers(memberName)
            .FirstOrDefault(candidate => candidate is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false });
        var memberOriginalType = member?.OriginalDefinition switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null
        };
        if (member == null || memberOriginalType == null)
            return true;

        for (var containing = member.ContainingType; containing != null; containing = containing.ContainingType)
        {
            var typeParameters = containing.OriginalDefinition.TypeParameters;
            var typeArguments = containing.TypeArguments;
            for (var i = 0; i < typeParameters.Length && i < typeArguments.Length; i++)
            {
                if (!TypeContainsOrEquals(memberOriginalType, typeParameters[i]))
                    continue;

                foreach (var methodTypeParameter in methodTypeParameters)
                {
                    if (TypeContainsOrEquals(typeArguments[i], methodTypeParameter))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the type of a named member / tuple element on
    /// <paramref name="returnType"/> (constructed method return).
    /// </summary>
    private static bool TryGetReturnMemberType(
        ITypeSymbol returnType,
        string memberName,
        out ITypeSymbol? memberType)
    {
        memberType = null;

        if (returnType is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            for (var i = 0; i < tuple.TupleElements.Length; i++)
            {
                var element = tuple.TupleElements[i];
                if (string.Equals(element.Name, memberName, StringComparison.Ordinal) ||
                    string.Equals(
                        StripVerbatimPrefix(element.Name),
                        StripVerbatimPrefix(memberName),
                        StringComparison.Ordinal) ||
                    string.Equals($"Item{i + 1}", memberName, StringComparison.Ordinal))
                {
                    memberType = element.Type;
                    return true;
                }
            }

            return false;
        }

        if (returnType is INamedTypeSymbol named)
        {
            foreach (var member in named.GetMembers(memberName))
            {
                switch (member)
                {
                    case IFieldSymbol field when !field.IsStatic:
                        memberType = field.Type;
                        return true;
                    case IPropertySymbol property when !property.IsStatic:
                        memberType = property.Type;
                        return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetNamedMemberInitializer(
        ExpressionSyntax aggregateCreation,
        string memberName,
        SemanticModel semanticModel,
        out ExpressionSyntax? initializer)
    {
        initializer = null;
        aggregateCreation = UnwrapParenthesesAndNullForgiving(aggregateCreation);

        if (aggregateCreation is TupleExpressionSyntax tuple)
        {
            var tupleType = semanticModel.GetTypeInfo(tuple).Type as INamedTypeSymbol;
            for (var i = 0; i < tuple.Arguments.Count; i++)
            {
                var argument = tuple.Arguments[i];
                // Match explicit NameColon, positional ItemN, and inferred element
                // names (var wrapped = (point, 0); wrapped.point / wrapped.Item1).
                if (!TupleAggregateArgumentMatchesMember(argument, i, memberName, tupleType))
                    continue;

                initializer = argument.Expression;
                return true;
            }

            return false;
        }

        if (aggregateCreation is AnonymousObjectCreationExpressionSyntax anonymous)
        {
            foreach (var member in anonymous.Initializers)
            {
                if (!AnonymousAggregateMemberMatchesName(member, memberName))
                    continue;

                initializer = member.Expression;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool AnonymousAggregateMemberMatchesName(
        AnonymousObjectMemberDeclaratorSyntax member,
        string memberName)
    {
        if (member.NameEquals != null)
        {
            return string.Equals(
                member.NameEquals.Name.Identifier.ValueText,
                memberName,
                StringComparison.Ordinal);
        }

        var inferred = member.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null
        };

        return inferred != null &&
               string.Equals(inferred, memberName, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="memberName"/> names the tuple element at
    /// <paramref name="index"/> via NameColon, ItemN, or an inferred element name.
    /// </summary>
    private static bool TupleAggregateArgumentMatchesMember(
        ArgumentSyntax argument,
        int index,
        string memberName,
        INamedTypeSymbol? tupleType)
    {
        var named = argument.NameColon?.Name.Identifier.ValueText;
        if (string.Equals(named, memberName, StringComparison.Ordinal))
            return true;

        if (string.Equals("Item" + (index + 1), memberName, StringComparison.Ordinal))
            return true;

        if (tupleType is { IsTupleType: true } &&
            !tupleType.TupleElements.IsDefault &&
            index < tupleType.TupleElements.Length &&
            string.Equals(tupleType.TupleElements[index].Name, memberName, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    internal static bool HasElementByRefUse(
        SyntaxNode root,
        SemanticModel semanticModel,
        ISymbol storage,
        HashSet<string> memberNames,
        ITypeSymbol? tupleType = null) =>
        HasElementByRefUse(
            root,
            semanticModel,
            new HashSet<ISymbol>(SymbolEqualityComparer.Default) { storage },
            memberNames,
            tupleType);

    /// <summary>
    /// True when a converted storage alias has a tuple element used as a
    /// <c>ref</c>/<c>out</c>/<c>in</c> argument, address-of operand, ref expression,
    /// writable <c>this ref</c> extension receiver, pointer indirection through a
    /// tracked address-of alias (<c>var p = &amp;point; Bump(ref p-&gt;X)</c> /
    /// <c>Bump(ref (*p).X)</c>),
    /// non-<c>readonly</c> mutable value-type instance-method receiver (including
    /// positional <c>ItemN</c> aliases), a non-<c>readonly</c> property / indexer
    /// getter on a mutable value-type element (e.g. <c>point.Counter.Next</c>),
    /// an implicit <c>foreach</c> / <c>await foreach</c> <c>GetEnumerator</c> /
    /// <c>GetAsyncEnumerator</c> on a mutable value-type element (e.g.
    /// <c>foreach (var item in point.Sequence)</c>), an implicit <c>using</c> /
    /// <c>await using</c> <c>Dispose</c> / <c>DisposeAsync</c> on a mutable
    /// value-type element expression, or an assignment / increment / decrement
    /// target that traverses a value-type converted element (for example
    /// <c>point.Counter.Value++</c> — CS1612 on the property temporary).
    /// Generated properties cannot accept those uses (CS0206 / silent copy / CS1612);
    /// tuple fields can. Direct element writes such as <c>point.X = 3</c> /
    /// <c>point.X++</c> are allowed under <c>asRecord: false</c> (setters work).
    /// By-ref storage reached <em>through</em> a reference-type element
    /// (for example <c>Bump(ref point.Values[0])</c>) or through a value-type
    /// element that merely wraps reference storage (for example
    /// <c>Bump(ref point.Box.Values[0])</c> where <c>Box</c> is a struct with an
    /// <c>int[]</c> field) is allowed — copying the property still yields the same
    /// reference — while passing the element property itself by reference, or
    /// taking a ref to a value-typed location on that temporary copy, is refused.
    /// Nested assignments that reach the same reference storage through a value-type
    /// element follow the same allow/refuse rule via <see cref="ValueTypeElementByRefIsUnsafe"/>.
    /// </summary>
    internal static bool HasElementByRefUse(
        SyntaxNode root,
        SemanticModel semanticModel,
        HashSet<ISymbol> storages,
        HashSet<string> memberNames,
        ITypeSymbol? tupleType = null)
    {
        foreach (var node in root.DescendantNodes())
        {
            ExpressionSyntax? byRefTarget = node switch
            {
                // Tuple fields accept address-of; generated properties do not.
                PrefixUnaryExpressionSyntax addressOf when
                    addressOf.IsKind(SyntaxKind.AddressOfExpression) => addressOf.Operand,
                // Tuple fields accept ref/out/in; generated properties do not.
                ArgumentSyntax argument when
                    argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
                    argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword) ||
                    argument.RefOrOutKeyword.IsKind(SyntaxKind.InKeyword) => argument.Expression,
                // ref int x = ref point.X; / ref returns
                RefExpressionSyntax refExpression => refExpression.Expression,
                // __makeref(point.X) requires a variable; generated properties are not (Codex P2).
                MakeRefExpressionSyntax makeRef => makeRef.Expression,
                // this-ref extension or mutating value-type instance-method receiver.
                InvocationExpressionSyntax invocation when
                    TryGetRefReceiverExpression(invocation, semanticModel, out var refReceiver)
                    => refReceiver,
                // Non-readonly property / indexer getter on a mutable value-type element
                // (point.Counter.Next) — getter receives the element by ref (Codex P1).
                MemberAccessExpressionSyntax memberAccess when
                    TryGetMutatingPropertyOrIndexerReceiver(memberAccess, semanticModel, out var propReceiver)
                    => propReceiver,
                ElementAccessExpressionSyntax elementAccess when
                    TryGetMutatingPropertyOrIndexerReceiver(elementAccess, semanticModel, out var indexReceiver)
                    => indexReceiver,
                // foreach / await foreach implicitly calls GetEnumerator on the collection
                // with no InvocationExpressionSyntax — a non-readonly GetEnumerator on a
                // mutable value-type element mutates a property temporary after conversion
                // (Codex P1 on #3441, PRRT_kwDORD0nYs606U0c).
                CommonForEachStatementSyntax forEach when
                    TryGetMutatingForeachCollectionReceiver(forEach, semanticModel, out var foreachReceiver)
                    => foreachReceiver,
                // using (point.Resource) / await using (point.Resource) implicitly calls
                // Dispose / DisposeAsync on a variable resource expression — same silent
                // copy hazard when the element becomes a property (Codex P1 sibling).
                UsingStatementSyntax usingStatement when
                    TryGetMutatingUsingResourceReceiver(usingStatement, semanticModel, out var usingReceiver)
                    => usingReceiver,
                // Nested field writes through a value-type element (Codex P1 CS1612):
                // point.Counter.Value++ / point.Counter.Value = 1 mutate a property temporary.
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax prefix when
                    prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                    prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                    postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
                _ => null
            };

            if (byRefTarget == null)
                continue;

            // Direct element writes (point.X = 3 / point.X++) stay allowed for
            // asRecord:false setters; only nested writes through a value-type
            // element are refused (same ValueTypeElementByRefIsUnsafe rule).
            var refuseDirectElementAccess = !IsAssignmentOrIncrementTarget(node);

            if (HasConvertedElementByRefAccess(
                    byRefTarget,
                    storages,
                    memberNames,
                    tupleType,
                    semanticModel,
                    root,
                    refuseDirectElementAccess))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="node"/> is an assignment or pre/post increment/decrement
    /// (the nested-value-type CS1612 cases), not address-of or other prefix/postfix forms.
    /// </summary>
    private static bool IsAssignmentOrIncrementTarget(SyntaxNode node) =>
        node is AssignmentExpressionSyntax
        || (node is PrefixUnaryExpressionSyntax prefix &&
            (prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
             prefix.IsKind(SyntaxKind.PreDecrementExpression)))
        || (node is PostfixUnaryExpressionSyntax postfix &&
            (postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
             postfix.IsKind(SyntaxKind.PostDecrementExpression)));

    /// <summary>
    /// True when <paramref name="invocation"/> uses its receiver by reference:
    /// a reduced extension with writable <c>this ref</c>
    /// (for example <c>point.X.Increment()</c> with <c>Increment(this ref int)</c>),
    /// or a non-<c>readonly</c> instance method on a mutable value-type element
    /// (for example <c>point.Counter.Increment()</c> on a mutable struct field —
    /// tuple fields persist the mutation; generated properties mutate a temporary),
    /// including when the element is a type parameter constrained to a value type
    /// whose resolved method lives on an interface constraint
    /// (for example <c>where T : struct, ICounter</c> — <c>ContainingType</c> is
    /// <c>ICounter</c>, but the call still mutates the tuple field of type <c>T</c>).
    /// Readonly <c>this in</c> / <c>ref readonly</c> receivers are allowed — a
    /// generated property is a valid value receiver with the same readonly behavior
    /// (Codex P2 on #3441).
    /// </summary>
    private static bool TryGetRefReceiverExpression(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        out ExpressionSyntax? receiver)
    {
        receiver = null;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            return false;

        // Reduced extension calls hide the `this` parameter; inspect ReducedFrom
        // (or the unreduced extension declaration) for writable `this ref` only.
        var extensionSource = method.ReducedFrom
            ?? (method.IsExtensionMethod ? method : null);
        if (extensionSource != null && !extensionSource.Parameters.IsDefaultOrEmpty)
        {
            var thisParameter = extensionSource.Parameters[0];
            if (thisParameter.RefKind == RefKind.Ref)
            {
                receiver = memberAccess.Expression;
                return true;
            }
        }

        // Non-readonly instance method whose receiver is a mutable value type —
        // either the method's containing type is that value type, or the receiver
        // is a type parameter constrained to a value type whose method resolves
        // on an interface constraint (Codex P1 constrained T : struct, ICounter).
        if (!method.IsStatic &&
            !method.IsReadOnly &&
            (method.MethodKind is MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation) &&
            IsMutatingValueTypeReceiver(
                method.ContainingType,
                semanticModel.GetTypeInfo(memberAccess.Expression).Type))
        {
            receiver = memberAccess.Expression;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when an instance-method receiver is a mutable value type after conversion
    /// would turn the element into a property temporary: the method is owned by a
    /// mutable value type, or the receiver is a type parameter known to be a value
    /// type (e.g. <c>where T : struct</c> / constrained to a struct) even when the
    /// resolved method lives on an interface constraint.
    /// </summary>
    private static bool IsMutatingValueTypeReceiver(
        INamedTypeSymbol? containingType,
        ITypeSymbol? receiverType)
    {
        if (containingType is { IsValueType: true, IsReadOnly: false })
            return true;

        // Constrained type-parameter receivers: ContainingType is often an interface
        // (ICounter), but T is a value type so the call mutates the tuple field.
        return receiverType is ITypeParameterSymbol { IsValueType: true };
    }

    /// <summary>
    /// True when <paramref name="expression"/> is a non-<c>readonly</c> property or
    /// indexer getter whose receiver is a mutable value type — the getter receives
    /// that receiver by reference (e.g. <c>point.Counter.Next</c> where <c>Next</c>
    /// increments a field). After conversion the element is a property temporary, so
    /// the getter mutates a copy (Codex P1 on #3441). Matches the mutating-method rule.
    /// </summary>
    private static bool TryGetMutatingPropertyOrIndexerReceiver(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out ExpressionSyntax? receiver)
    {
        receiver = null;

        ExpressionSyntax? receiverExpression = expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            ElementAccessExpressionSyntax elementAccess => elementAccess.Expression,
            _ => null
        };
        if (receiverExpression == null)
            return false;

        if (semanticModel.GetSymbolInfo(expression).Symbol is not IPropertySymbol property)
            return false;

        // IsReadOnly on IPropertySymbol means get-only (no setter) — unrelated.
        // The getter's own IsReadOnly controls whether `this` is passed by ref.
        if (property.GetMethod is not { IsReadOnly: false })
            return false;

        if (!IsMutatingValueTypeReceiver(
                property.ContainingType,
                semanticModel.GetTypeInfo(receiverExpression).Type))
        {
            return false;
        }

        receiver = receiverExpression;
        return true;
    }

    /// <summary>
    /// True when a <c>foreach</c> / <c>await foreach</c> collection expression is a
    /// mutable value-type element whose resolved <c>GetEnumerator</c> /
    /// <c>GetAsyncEnumerator</c> is a non-<c>readonly</c> instance method — the
    /// compiler-generated call has no <see cref="InvocationExpressionSyntax"/>, so
    /// the ordinary mutating-receiver switch misses it (Codex P1 on #3441,
    /// PRRT_kwDORD0nYs606U0c). After conversion the element is a property temporary
    /// and the enumerator factory mutates only that copy.
    /// </summary>
    private static bool TryGetMutatingForeachCollectionReceiver(
        CommonForEachStatementSyntax forEach,
        SemanticModel semanticModel,
        out ExpressionSyntax? receiver)
    {
        receiver = null;
        var info = semanticModel.GetForEachStatementInfo(forEach);
        // ForEachStatementInfo exposes GetEnumeratorMethod for both sync foreach and
        // await foreach (where it is GetAsyncEnumerator). Fall back to a type lookup
        // when the info is incomplete.
        var enumeratorMethod = info.GetEnumeratorMethod
            ?? ResolveEnumeratorMethod(forEach.Expression, semanticModel, isAsync: forEach.AwaitKeyword != default);
        if (enumeratorMethod == null ||
            enumeratorMethod.IsStatic ||
            enumeratorMethod.IsReadOnly)
        {
            return false;
        }

        if (!IsMutatingValueTypeReceiver(
                enumeratorMethod.ContainingType,
                semanticModel.GetTypeInfo(forEach.Expression).Type))
        {
            return false;
        }

        receiver = forEach.Expression;
        return true;
    }

    /// <summary>
    /// Resolves <c>GetEnumerator</c> / <c>GetAsyncEnumerator</c> on the collection
    /// expression's type when <see cref="ForEachStatementInfo"/> is incomplete.
    /// </summary>
    private static IMethodSymbol? ResolveEnumeratorMethod(
        ExpressionSyntax collection,
        SemanticModel semanticModel,
        bool isAsync)
    {
        var type = semanticModel.GetTypeInfo(collection).Type;
        if (type == null)
            return null;

        var name = isAsync ? "GetAsyncEnumerator" : "GetEnumerator";
        foreach (var member in type.GetMembers(name).OfType<IMethodSymbol>())
        {
            if (!member.IsStatic)
                return member;
        }

        return null;
    }

    /// <summary>
    /// True when a <c>using (expr)</c> / <c>await using (expr)</c> resource expression
    /// (not a declaration) is a mutable value-type element whose resolved
    /// <c>Dispose</c> / <c>DisposeAsync</c> is a non-<c>readonly</c> instance method.
    /// Declaration forms (<c>using var x = …</c> / <c>using (var x = …)</c>) Dispose the
    /// declared local copy both before and after conversion, so they are allowed.
    /// </summary>
    private static bool TryGetMutatingUsingResourceReceiver(
        UsingStatementSyntax usingStatement,
        SemanticModel semanticModel,
        out ExpressionSyntax? receiver)
    {
        receiver = null;
        // Declaration forms dispose a local copy — behavior is unchanged after conversion.
        if (usingStatement.Declaration != null || usingStatement.Expression == null)
            return false;

        var resource = usingStatement.Expression;
        var disposeMethod = ResolveDisposeMethod(resource, semanticModel);
        if (disposeMethod == null ||
            disposeMethod.IsStatic ||
            disposeMethod.IsReadOnly)
        {
            return false;
        }

        if (!IsMutatingValueTypeReceiver(
                disposeMethod.ContainingType,
                semanticModel.GetTypeInfo(resource).Type))
        {
            return false;
        }

        receiver = resource;
        return true;
    }

    /// <summary>
    /// Resolves the instance <c>Dispose</c> / <c>DisposeAsync</c> that a
    /// <c>using</c> / <c>await using</c> statement would invoke on
    /// <paramref name="resource"/> (interface or pattern-based).
    /// </summary>
    private static IMethodSymbol? ResolveDisposeMethod(
        ExpressionSyntax resource,
        SemanticModel semanticModel)
    {
        var type = semanticModel.GetTypeInfo(resource).Type;
        if (type == null)
            return null;

        // Prefer pattern-based Dispose / DisposeAsync on the concrete type, then
        // IDisposable / IAsyncDisposable interface members.
        foreach (var name in new[] { "DisposeAsync", "Dispose" })
        {
            foreach (var member in type.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (!member.IsStatic && member.Parameters.IsDefaultOrEmpty)
                    return member;
            }
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.SpecialType == SpecialType.System_IDisposable ||
                (iface.Name is "IAsyncDisposable" &&
                 iface.ContainingNamespace?.ToDisplayString() == "System"))
            {
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (!member.IsStatic && member.Parameters.IsDefaultOrEmpty &&
                        member.Name is "Dispose" or "DisposeAsync")
                    {
                        return member;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="expression"/> reaches a converted tuple element
    /// through converted storage (or a container member that carries it), not
    /// through an unrelated same-named member of a larger aggregate that merely
    /// embeds the converted value (Codex: <c>var wrapped = (point, X: 5); ref wrapped.X</c>).
    /// </summary>
    private static bool HasConvertedElementMemberAccess(
        ExpressionSyntax expression,
        HashSet<ISymbol> storages,
        HashSet<string> memberNames,
        ITypeSymbol? tupleType,
        SemanticModel semanticModel,
        SyntaxNode root)
    {
        foreach (var memberAccess in EnumerateAssignmentMemberAccesses(expression))
        {
            if (!memberNames.Contains(memberAccess.Name.Identifier.ValueText))
                continue;

            // Direct storage, or carrier members (tuple / anonymous-object,
            // including nested carriers) that embed converted storage.
            if (ExpressionYieldsConvertedStorage(
                    memberAccess.Expression,
                    storages,
                    tupleType,
                    semanticModel,
                    root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="byRefTarget"/> is a converted tuple element used
    /// in a way that breaks after conversion to properties: the element property
    /// itself is the by-ref target, or a value-typed location on a temporary
    /// property copy. False when the by-ref target is deeper storage reached
    /// through a reference-type element (Codex: <c>Bump(ref point.Values[0])</c>)
    /// or through a value-type element that merely wraps reference storage
    /// (Codex P2: <c>Bump(ref point.Box.Values[0])</c>).
    /// </summary>
    private static bool HasConvertedElementByRefAccess(
        ExpressionSyntax byRefTarget,
        HashSet<ISymbol> storages,
        HashSet<string> memberNames,
        ITypeSymbol? tupleType,
        SemanticModel semanticModel,
        SyntaxNode root,
        bool refuseDirectElementAccess = true)
    {
        var coreTarget = UnwrapParenthesesAndNullForgiving(byRefTarget);

        // Deconstruction assignment: (point.X, point.Y) = (3, 4) writes each
        // destination like point.X = 3 (Codex P2 on #3441), including nested tuples.
        var deconstructionDestinations = coreTarget is TupleExpressionSyntax tupleTarget
            ? CollectDeconstructionDestinations(tupleTarget)
            : null;

        foreach (var memberAccess in EnumerateAssignmentMemberAccesses(byRefTarget))
        {
            if (!memberNames.Contains(memberAccess.Name.Identifier.ValueText))
                continue;

            if (!ExpressionYieldsConvertedStorage(
                    memberAccess.Expression,
                    storages,
                    tupleType,
                    semanticModel,
                    root))
            {
                continue;
            }

            // Direct by-ref / address-of on the element property itself — refuse.
            // Assignment / ++/-- on the element itself are allowed (setters); only
            // nested writes through a value-type element hit the path below.
            if (coreTarget == memberAccess)
                return refuseDirectElementAccess;

            var effectiveTarget = coreTarget;
            if (deconstructionDestinations != null)
            {
                if (deconstructionDestinations.Contains(memberAccess))
                {
                    if (refuseDirectElementAccess)
                        return true;
                    continue;
                }

                // Nested write inside one destination: evaluate against that destination.
                effectiveTarget = FindContainingDeconstructionDestination(
                    memberAccess, deconstructionDestinations) ?? coreTarget;
            }

            // Through the element: reference-type property returns the same
            // reference — allow. Value-type property returns a copy — allow only
            // when the remaining path reaches nested reference storage (array /
            // class field) before the by-ref target; otherwise refuse.
            var elementType = semanticModel.GetTypeInfo(memberAccess).Type;
            if (elementType is { IsReferenceType: true })
                continue;

            if (elementType is { IsValueType: true } &&
                !ValueTypeElementByRefIsUnsafe(memberAccess, effectiveTarget, semanticModel))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// True when by-ref through a value-type converted element still depends on
    /// the temporary property copy (unsafe after conversion). False when the
    /// remaining access path reaches reference storage first — copying the
    /// value-type element preserves that shared reference
    /// (<c>Bump(ref point.Box.Values[0])</c>).
    /// </summary>
    private static bool ValueTypeElementByRefIsUnsafe(
        MemberAccessExpressionSyntax elementAccess,
        ExpressionSyntax coreTarget,
        SemanticModel semanticModel)
    {
        ExpressionSyntax current = elementAccess;
        while (!ReferenceEquals(current, coreTarget))
        {
            var parent = current.Parent;

            if (parent is ParenthesizedExpressionSyntax parenthesized)
            {
                current = parenthesized;
                continue;
            }

            if (parent is PostfixUnaryExpressionSyntax postfix &&
                postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                current = postfix;
                continue;
            }

            if (parent is CheckedExpressionSyntax checkedExpression)
            {
                current = checkedExpression;
                continue;
            }

            if (parent is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression == current)
            {
                var memberType = semanticModel.GetTypeInfo(memberAccess).Type;
                if (memberType is { IsReferenceType: true })
                {
                    // ref to the reference-typed member of a value-type temporary
                    // (e.g. ref point.Box.Values) is unsafe; ref through it into
                    // deeper storage (e.g. ref point.Box.Values[0]) is safe.
                    return ReferenceEquals(memberAccess, coreTarget);
                }

                current = memberAccess;
                continue;
            }

            if (parent is ElementAccessExpressionSyntax element &&
                element.Expression == current)
            {
                var indexedType = semanticModel.GetTypeInfo(current).Type;
                if (indexedType is { IsReferenceType: true })
                    return false; // ref into shared array / indexer storage

                current = element;
                continue;
            }

            // Unknown shape — refuse to stay safe.
            return true;
        }

        // Reached the by-ref target without finding nested reference storage.
        return true;
    }

    /// <summary>
    /// Collects the leaf destinations of a deconstruction assignment left-hand side
    /// (<c>(point.X, (point.Y, other))</c>), unwrapping parentheses / null-forgiving.
    /// </summary>
    private static HashSet<ExpressionSyntax> CollectDeconstructionDestinations(TupleExpressionSyntax tuple)
    {
        var destinations = new HashSet<ExpressionSyntax>();
        var pending = new Stack<TupleExpressionSyntax>();
        pending.Push(tuple);
        while (pending.Count > 0)
        {
            foreach (var argument in pending.Pop().Arguments)
            {
                var destination = UnwrapParenthesesAndNullForgiving(argument.Expression);
                if (destination is TupleExpressionSyntax nested)
                    pending.Push(nested);
                else
                    destinations.Add(destination);
            }
        }

        return destinations;
    }

    private static ExpressionSyntax? FindContainingDeconstructionDestination(
        SyntaxNode node,
        HashSet<ExpressionSyntax> destinations)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is ExpressionSyntax expression && destinations.Contains(expression))
                return expression;
        }

        return null;
    }

    private static ExpressionSyntax UnwrapParenthesesAndNullForgiving(ExpressionSyntax expression) =>
        SkipTransparentWrappers(expression);

    /// <summary>
    /// True when <paramref name="receiver"/> is the converted tuple value (or a
    /// value-alias / container element that yields that type), not a larger
    /// aggregate that only embeds converted storage.
    /// </summary>
    private static bool ReceiverYieldsConvertedStorage(
        ExpressionSyntax receiver,
        HashSet<ISymbol> storages,
        ITypeSymbol? tupleType,
        SemanticModel semanticModel)
    {
        var receiverType = semanticModel.GetTypeInfo(receiver).Type;
        if (tupleType != null && receiverType != null &&
            !SymbolEqualityComparer.Default.Equals(receiverType, tupleType))
        {
            // Pointer aliases (var p = &point; Bump(ref p->X) / Bump(ref (*p).X)) —
            // pointee matches the converted tuple type, so p->Element / (*p).Element
            // is a by-ref use of storage (Codex P1 on #3441). Larger aggregates that
            // merely embed storage still fall through to the refuse-on-mismatch path below.
            if (receiverType is IPointerTypeSymbol pointer &&
                SymbolEqualityComparer.Default.Equals(pointer.PointedAtType, tupleType))
            {
                // Fall through to symbol / alias checks.
            }
            // dynamic copy = point; Bump(ref copy.Item1) — type is dynamic, not the
            // tuple, but the collector still tracks copy; consult the symbol before
            // rejecting (Codex P1 on #3441).
            else if (receiverType.TypeKind == TypeKind.Dynamic)
            {
                // Fall through to symbol / alias checks.
            }
            else
            {
                // Larger aggregate (e.g. wrapped in (point, X: 5)) — not the
                // converted storage itself; element checks must use
                // ExpressionYieldsConvertedStorage on the carrier member.
                return false;
            }
        }

        var target = semanticModel.GetSymbolInfo(receiver).Symbol;
        if (target != null && storages.Contains(target))
            return true;

        return ExpressionReferencesStorageAlias(receiver, storages, semanticModel);
    }

    internal static bool HasElementWrite(
        SyntaxNode root,
        SemanticModel semanticModel,
        ISymbol storage,
        HashSet<string> memberNames,
        ITypeSymbol? tupleType = null) =>
        HasElementWrite(
            root,
            semanticModel,
            new HashSet<ISymbol>(SymbolEqualityComparer.Default) { storage },
            memberNames,
            tupleType);

    internal static bool HasElementWrite(
        SyntaxNode root,
        SemanticModel semanticModel,
        HashSet<ISymbol> storages,
        HashSet<string> memberNames,
        ITypeSymbol? tupleType = null)
    {
        foreach (var node in root.DescendantNodes())
        {
            ExpressionSyntax? writtenTarget = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax prefix when
                    prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                    prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                    postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
                // Tuple fields accept address-of; generated properties do not.
                PrefixUnaryExpressionSyntax addressOf when
                    addressOf.IsKind(SyntaxKind.AddressOfExpression) => addressOf.Operand,
                // Tuple fields accept ref/out/in; generated properties do not.
                ArgumentSyntax argument when
                    argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) ||
                    argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword) ||
                    argument.RefOrOutKeyword.IsKind(SyntaxKind.InKeyword) => argument.Expression,
                // ref int x = ref point.X; / ref returns
                RefExpressionSyntax refExpression => refExpression.Expression,
                // this-ref extension or mutating value-type instance-method receiver.
                InvocationExpressionSyntax invocation when
                    TryGetRefReceiverExpression(invocation, semanticModel, out var refReceiver)
                    => refReceiver,
                // Non-readonly property / indexer getter on a mutable value-type element (Codex P1).
                MemberAccessExpressionSyntax memberAccess when
                    TryGetMutatingPropertyOrIndexerReceiver(memberAccess, semanticModel, out var propReceiver)
                    => propReceiver,
                ElementAccessExpressionSyntax elementAccess when
                    TryGetMutatingPropertyOrIndexerReceiver(elementAccess, semanticModel, out var indexReceiver)
                    => indexReceiver,
                _ => null
            };

            if (writtenTarget == null)
                continue;

            // Direct member writes (point.X = 3), nested deconstruction
            // targets ((point.X, point.Y) = (3, 4)), chained mutable-value
            // writes (point.X.Value = 3), inferred-container writes
            // (points[0].X = 3), address-of (&point.X), ref/out/in arguments,
            // ref expressions, and this-ref extension receivers all need
            // rejection for init-only record struct properties /
            // property-not-variable errors. Gate receivers on the converted
            // tuple type so unrelated same-named members of a larger
            // aggregate (wrapped.X beside embedded point) are not false hits.
            if (HasConvertedElementMemberAccess(
                    writtenTarget,
                    storages,
                    memberNames,
                    tupleType,
                    semanticModel,
                    root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Yields member accesses that are assignment / increment targets, including
    /// nested accesses inside tuple deconstruction left-hand sides and inner
    /// accesses of chained targets (e.g. <c>point.X.Value</c> also yields <c>point.X</c>).
    /// </summary>
    internal static IEnumerable<MemberAccessExpressionSyntax> EnumerateAssignmentMemberAccesses(
        ExpressionSyntax writtenTarget)
    {
        if (writtenTarget is MemberAccessExpressionSyntax direct)
            yield return direct;

        foreach (var nested in writtenTarget.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            yield return nested;
    }

    /// <summary>
    /// <c>record struct</c> / init object-initializers require C# 10+ in every
    /// document that will be rewritten (declaration site and same-shape creations).
    /// Latest / Preview / LatestMajor are allowed.
    /// </summary>
    private static async Task ValidateRecordStructLanguageVersionAsync(
        IReadOnlyList<CreationTarget> creations,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<DocumentId>();
        foreach (var target in creations)
        {
            if (!seen.Add(target.Document.Id))
                continue;

            var model = await target.Document.GetSemanticModelAsync(cancellationToken);
            if (model != null)
                ValidateRecordStructLanguageVersion(model);
        }
    }

    /// <summary>
    /// <c>record struct</c> requires C# 10+. Latest / Preview / LatestMajor are allowed.
    /// </summary>
    internal static void ValidateRecordStructLanguageVersion(SemanticModel semanticModel)
    {
        if (semanticModel.SyntaxTree.Options is not CSharpParseOptions parseOptions)
            return;

        var version = parseOptions.LanguageVersion;
        if (version is LanguageVersion.Latest or LanguageVersion.LatestMajor or LanguageVersion.Preview)
            return;

        if (version < LanguageVersion.CSharp10)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "asRecord requires C# 10 or later (record structs are not available in earlier language versions).");
        }
    }

    /// <summary>
    /// Names that collide with synthesized <c>record struct</c> members (CS8859 / CS0102).
    /// </summary>
    private static readonly HashSet<string> RecordReservedMemberNames = new(StringComparer.Ordinal)
    {
        "Clone",
        "Equals",
        "GetHashCode",
        "PrintMembers",
        "ToString",
        "op_Equality",
        "op_Inequality"
    };

    internal static void ValidateMembersForGeneratedType(
        IReadOnlyList<TupleMember> members,
        SemanticModel semanticModel,
        int insertPosition,
        bool asRecord)
    {
        foreach (var member in members)
        {
            if (asRecord && RecordReservedMemberNames.Contains(StripVerbatimPrefix(member.Name)))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Tuple member '{member.Name}' cannot be used when asRecord is true because it collides with a synthesized record member.");
            }

            if (ContextValidTypeHelpers.IsLessAccessibleThanPublic(member.Type))
            {
                throw new RefactoringException(
                    ErrorCodes.BreaksAccessibility,
                    $"Tuple member type '{member.Type.ToDisplayString()}' is less accessible than the generated public type.");
            }

            if (!ContextValidTypeHelpers.MemberTypeBindsAtInsertion(member.Type, semanticModel, insertPosition))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Tuple member type '{member.Type.ToDisplayString()}' is not available at the generated type location.");
            }
        }
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
    /// Element names used by write / by-ref gates: each friendly name plus its
    /// positional <c>ItemN</c> alias so <c>Bump(ref point.Item1)</c> on a named
    /// tuple <c>(X: 1, Y: 2)</c> is still detected (Codex P1).
    /// </summary>
    internal static HashSet<string> BuildElementMemberNames(IReadOnlyList<TupleMember> members)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < members.Count; i++)
        {
            names.Add(StripVerbatimPrefix(members[i].Name));
            names.Add($"Item{i + 1}");
        }

        return names;
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
            if (NamespaceEqualityHelpers.NamespacesEqual(symbol.ContainingNamespace, targetNamespace))
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

        if (!string.IsNullOrEmpty(targetNamespace) && HasUsingInScope(creation, targetNamespace))
            return newTypeName;

        return string.IsNullOrEmpty(targetNamespace)
            ? newTypeName
            : $"{targetNamespace}.{newTypeName}";
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
                    Description = $"Convert tuple to {(@params.AsRecord ? "record struct" : "struct")} '{@params.NewTypeName}'",
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
                    Description = $"Create {(@params.AsRecord ? "record struct" : "struct")} '{@params.NewTypeName}'",
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
                Description = $"Convert tuple to {(@params.AsRecord ? "record struct" : "struct")} '{@params.NewTypeName}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record CreationTarget(Document Document, TextSpan Span);
}

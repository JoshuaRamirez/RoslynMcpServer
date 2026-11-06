using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        ValidateMembersForGeneratedType(members, semanticModel, insertPosition, @params.AsRecord);
        if (@params.AsRecord)
            await ValidateNoInitBreakingElementWritesAsync(creations, members, tupleType, cancellationToken);
        else
            await ValidateNoByRefElementUsesAsync(creations, members, tupleType, cancellationToken);
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

            var storage = TryGetAssignedStorageSymbol(creation, model, cancellationToken);
            if (storage == null)
                continue;

            var storages = CollectStorageAliases(root, model, storage, cancellationToken);
            if (HasElementWrite(root, model, storages, memberNames, tupleType))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Cannot convert to a record struct because '{storage.Name}' has element assignments that require settable properties.");
            }
        }
    }

    /// <summary>
    /// Named structs emit <c>{ get; set; }</c> properties; reject when a converted
    /// creation's storage (or alias) later passes a tuple element by <c>ref</c>/<c>out</c>/<c>in</c>,
    /// takes its address, or binds it to a ref local/return (CS0206). Plain assignments and
    /// increments remain allowed for non-record structs.
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

            var storage = TryGetAssignedStorageSymbol(creation, model, cancellationToken);
            if (storage == null)
                continue;

            var storages = CollectStorageAliases(root, model, storage, cancellationToken);
            if (HasElementByRefUse(root, model, storages, memberNames, tupleType))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"Cannot convert because '{storage.Name}' has an element passed by reference; the generated struct exposes properties, not fields.");
            }
        }
    }

    internal static ISymbol? TryGetAssignedStorageSymbol(
        ExpressionSyntax creation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken = default)
    {
        SyntaxNode node = creation;
        while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
            node = parenthesized;

        if (node.Parent is EqualsValueClauseSyntax equals)
        {
            switch (equals.Parent)
            {
                case VariableDeclaratorSyntax declarator:
                    return semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
                case PropertyDeclarationSyntax property:
                    return semanticModel.GetDeclaredSymbol(property, cancellationToken);
            }
        }

        if (node.Parent is AssignmentExpressionSyntax assignment &&
            assignment.Right == node)
        {
            return semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
        }

        return null;
    }

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
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in root.DescendantNodes())
            {
                ISymbol? alias = null;
                ExpressionSyntax? source = null;

                if (node is VariableDeclaratorSyntax declarator &&
                    declarator.Initializer?.Value is { } init)
                {
                    alias = semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
                    source = init;
                }
                else if (node is AssignmentExpressionSyntax assignment &&
                         assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    // var (copy, _) = (point, 0); / (copy, _) = … / (var copy, var _) = …
                    if (TryAddDeconstructionAliasesFromAssignment(
                            assignment.Left,
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
                else if (node is ForEachStatementSyntax forEach &&
                         forEach.Type is RefTypeSyntax)
                {
                    // foreach (ref var p in points.AsSpan()) p.X = 3;
                    alias = semanticModel.GetDeclaredSymbol(forEach, cancellationToken);
                    source = forEach.Expression;
                }
                else if (node is IsPatternExpressionSyntax isPattern)
                {
                    // if (point is var copy) / point is { } copy (Codex P1)
                    if (TryAddPatternDesignationAliases(
                            isPattern.Pattern,
                            isPattern.Expression,
                            aliases,
                            semanticModel,
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
                            cancellationToken))
                    {
                        changed = true;
                        continue;
                    }
                }

                if (alias == null || source == null || aliases.Contains(alias))
                    continue;

                while (true)
                {
                    if (source is ParenthesizedExpressionSyntax parenthesized)
                        source = parenthesized.Expression;
                    else if (source is RefExpressionSyntax refExpression)
                        source = refExpression.Expression;
                    else
                        break;
                }

                if (ExpressionReferencesStorageAlias(source, aliases, semanticModel, cancellationToken))
                {
                    aliases.Add(alias);
                    changed = true;
                }
            }
        }

        return aliases;
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
        CancellationToken cancellationToken)
    {
        left = UnwrapParenthesesAndNullForgiving(left);

        if (left is DeclarationExpressionSyntax declaration &&
            declaration.Designation != null)
        {
            return TryAddDeconstructionAliases(
                declaration.Designation, right, aliases, semanticModel, cancellationToken);
        }

        if (left is TupleExpressionSyntax leftTuple)
        {
            right = UnwrapParenthesesAndRef(right);
            if (right is not TupleExpressionSyntax rightTuple ||
                leftTuple.Arguments.Count != rightTuple.Arguments.Count)
            {
                return false;
            }

            var added = false;
            for (var i = 0; i < leftTuple.Arguments.Count; i++)
            {
                var dest = leftTuple.Arguments[i].Expression;
                var src = rightTuple.Arguments[i].Expression;

                if (dest is DeclarationExpressionSyntax nestedDeclaration &&
                    nestedDeclaration.Designation != null)
                {
                    if (TryAddDeconstructionAliases(
                            nestedDeclaration.Designation,
                            src,
                            aliases,
                            semanticModel,
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
    /// </summary>
    private static bool TryAddDeconstructionAliases(
        VariableDesignationSyntax designation,
        ExpressionSyntax source,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
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
                    if (source is not TupleExpressionSyntax tuple ||
                        tuple.Arguments.Count != parenthesized.Variables.Count)
                    {
                        return false;
                    }

                    var added = false;
                    for (var i = 0; i < parenthesized.Variables.Count; i++)
                    {
                        if (TryAddDeconstructionAliases(
                                parenthesized.Variables[i],
                                tuple.Arguments[i].Expression,
                                aliases,
                                semanticModel,
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
    /// Adds aliases for pattern designations that copy converted storage
    /// (<c>if (point is var copy)</c>, <c>point is { } copy</c>, switch arms/cases).
    /// </summary>
    private static bool TryAddPatternDesignationAliases(
        PatternSyntax pattern,
        ExpressionSyntax input,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
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
                    cancellationToken);

            case DeclarationPatternSyntax declarationPattern when
                declarationPattern.Designation != null:
                return TryAddDeconstructionAliases(
                    declarationPattern.Designation,
                    input,
                    aliases,
                    semanticModel,
                    cancellationToken);

            case RecursivePatternSyntax recursive when recursive.Designation != null:
                return TryAddDeconstructionAliases(
                    recursive.Designation,
                    input,
                    aliases,
                    semanticModel,
                    cancellationToken);

            case BinaryPatternSyntax binary:
                {
                    var left = TryAddPatternDesignationAliases(
                        binary.Left, input, aliases, semanticModel, cancellationToken);
                    var right = TryAddPatternDesignationAliases(
                        binary.Right, input, aliases, semanticModel, cancellationToken);
                    return left || right;
                }

            default:
                return false;
        }
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
            if (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;
            else if (expression is RefExpressionSyntax refExpression)
                expression = refExpression.Expression;
            else
                return expression;
        }
    }

    /// <summary>
    /// True when <paramref name="expression"/> is (or is a conditional /
    /// switch / identity-like generic invocation / array-or-collection
    /// creation / container element read / container-forwarding call that
    /// forwards) a reference to a known storage alias.
    /// </summary>
    internal static bool ExpressionReferencesStorageAlias(
        ExpressionSyntax expression,
        HashSet<ISymbol> aliases,
        SemanticModel semanticModel,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;
            else if (expression is RefExpressionSyntax refExpression)
                expression = refExpression.Expression;
            else
                break;
        }

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
                        if (IsIdentitySelectInvocation(invocation, method) ||
                            IsIdentityConvertAllInvocation(invocation, method))
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
                if (MethodReturnWrapsElementAsConstructedGeneric(method, parameter))
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
                 IsIdentitySelectInvocation(invocation, method)))
            {
                return true;
            }

            // points.Select(x => x) — TSource/TResult differ but selector forwards.
            if (IsIdentitySelectInvocation(invocation, method))
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
        }

        // Array / collection creations that embed an alias so
        // var points = new[] { point }; points[0].X = 3; tracks points.
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

        if (expression is CollectionExpressionSyntax collection)
        {
            foreach (var element in collection.Elements.OfType<ExpressionElementSyntax>())
            {
                if (ExpressionReferencesStorageAlias(element.Expression, aliases, semanticModel, cancellationToken))
                    return true;
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

        // Reads from tracked containers: var copy = points[0]; copy.X = 3;
        if (expression is ElementAccessExpressionSyntax elementAccess)
        {
            return ExpressionReferencesStorageAlias(
                elementAccess.Expression, aliases, semanticModel, cancellationToken);
        }

        // Forwarding properties only (memory.Span). Do not treat every member
        // of an alias-containing aggregate as an alias (wrapped.Fresh) — Codex P2.
        if (expression is MemberAccessExpressionSyntax nestedAccess)
        {
            var memberName = nestedAccess.Name.Identifier.ValueText;
            // Span/Memory: container views. Result: Task<T>/ValueTask<T> sync unwrap.
            if (memberName is "Span" or "Memory" or "Result")
            {
                return ExpressionReferencesStorageAlias(
                    nestedAccess.Expression, aliases, semanticModel, cancellationToken);
            }
        }

        // Await unwrap: var copy = await Task.FromResult(point);
        if (expression is AwaitExpressionSyntax awaitExpression)
        {
            return ExpressionReferencesStorageAlias(
                awaitExpression.Expression, aliases, semanticModel, cancellationToken);
        }

        return false;
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
    /// True when <paramref name="parameter"/> is type parameter <c>T</c> and the
    /// method returns a constructed generic whose sole type argument is <c>T</c>
    /// (e.g. <c>Task.FromResult&lt;T&gt;</c>).
    /// </summary>
    private static bool MethodReturnWrapsElementAsConstructedGeneric(
        IMethodSymbol method,
        IParameterSymbol parameter)
    {
        var original = (method.ReducedFrom ?? method).OriginalDefinition;
        if (parameter.Ordinal < 0 || parameter.Ordinal >= original.Parameters.Length)
            return false;

        if (original.Parameters[parameter.Ordinal].Type is not ITypeParameterSymbol elementTypeParameter)
            return false;

        if (original.ReturnType is not INamedTypeSymbol { Arity: 1 } namedReturn)
            return false;

        return SymbolEqualityComparer.Default.Equals(
            namedReturn.TypeArguments[0],
            elementTypeParameter);
    }

    /// <summary>
    /// True when <paramref name="parameter"/> is an element type parameter <c>T</c>
    /// and the method returns a sequence of <c>T</c> (e.g. <c>Enumerable.Repeat</c>).
    /// </summary>
    private static bool MethodReturnWrapsElementAsSequence(IMethodSymbol method, IParameterSymbol parameter)
    {
        var original = (method.ReducedFrom ?? method).OriginalDefinition;
        if (parameter.Ordinal < 0 || parameter.Ordinal >= original.Parameters.Length)
            return false;

        if (original.Parameters[parameter.Ordinal].Type is not ITypeParameterSymbol elementTypeParameter)
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
    private static bool IsIdentitySelectInvocation(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        var name = (method.ReducedFrom ?? method).OriginalDefinition.Name;
        if (name is not ("Select" or "SelectMany"))
            return false;

        var lambdaIdentity = new List<bool>();
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var expr = ParenthesizedExpressionHelpers.SkipParentheses(argument.Expression);

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
            }
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

    private static bool IsIdentityConvertAllInvocation(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        var name = (method.ReducedFrom ?? method).OriginalDefinition.Name;
        if (name is not "ConvertAll")
            return false;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var expr = ParenthesizedExpressionHelpers.SkipParentheses(argument.Expression);

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
        }

        return false;
    }

    private static bool IsIdentityLambdaBody(
        ExpressionSyntax? expressionBody,
        BlockSyntax? block,
        string parameterName)
    {
        if (expressionBody != null)
        {
            var body = ParenthesizedExpressionHelpers.SkipParentheses(expressionBody);

            return body is IdentifierNameSyntax id &&
                   id.Identifier.ValueText == parameterName;
        }

        if (block?.Statements is [ReturnStatementSyntax { Expression: { } returned }])
        {
            return ParenthesizedExpressionHelpers.SkipParentheses(returned) is IdentifierNameSyntax id &&
                   id.Identifier.ValueText == parameterName;
        }

        return false;
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
        if (containerInitializer is ObjectCreationExpressionSyntax objectCreation &&
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
            }

            yield break;
        }

        if (containerInitializer is ObjectCreationExpressionSyntax objectCreation &&
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
    /// <c>this ref</c> extension receiver, or non-<c>readonly</c> mutable value-type
    /// instance-method receiver (including positional <c>ItemN</c> aliases).
    /// Generated properties cannot accept those uses (CS0206 / silent copy); tuple fields can.
    /// By-ref storage reached <em>through</em> a reference-type element
    /// (for example <c>Bump(ref point.Values[0])</c>) or through a value-type
    /// element that merely wraps reference storage (for example
    /// <c>Bump(ref point.Box.Values[0])</c> where <c>Box</c> is a struct with an
    /// <c>int[]</c> field) is allowed — copying the property still yields the same
    /// reference — while passing the element property itself by reference, or
    /// taking a ref to a value-typed location on that temporary copy, is refused.
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
                // this-ref extension or mutating value-type instance-method receiver.
                InvocationExpressionSyntax invocation when
                    TryGetRefReceiverExpression(invocation, semanticModel, out var refReceiver)
                    => refReceiver,
                _ => null
            };

            if (byRefTarget == null)
                continue;

            if (HasConvertedElementByRefAccess(
                    byRefTarget,
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
    /// True when <paramref name="invocation"/> uses its receiver by reference:
    /// a reduced extension with <c>this ref</c>/<c>in</c>/<c>ref readonly</c>
    /// (for example <c>point.X.Increment()</c> with <c>Increment(this ref int)</c>),
    /// or a non-<c>readonly</c> instance method on a mutable value-type element
    /// (for example <c>point.Counter.Increment()</c> on a mutable struct field —
    /// tuple fields persist the mutation; generated properties mutate a temporary).
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
        // (or the unreduced extension declaration) for `this ref` / `this in`.
        var extensionSource = method.ReducedFrom
            ?? (method.IsExtensionMethod ? method : null);
        if (extensionSource != null && !extensionSource.Parameters.IsDefaultOrEmpty)
        {
            var thisParameter = extensionSource.Parameters[0];
            if (thisParameter.RefKind is (
                    RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter))
            {
                receiver = memberAccess.Expression;
                return true;
            }
        }

        // Non-readonly instance method on a mutable value type — receiver is an
        // implicit ref (Codex P1: point.Counter.Increment() on a mutable struct).
        if (!method.IsStatic &&
            method.ContainingType is { IsValueType: true, IsReadOnly: false } &&
            !method.IsReadOnly &&
            method.MethodKind is MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation)
        {
            receiver = memberAccess.Expression;
            return true;
        }

        return false;
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
        SyntaxNode root)
    {
        var coreTarget = UnwrapParenthesesAndNullForgiving(byRefTarget);

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
            if (coreTarget == memberAccess)
                return true;

            // Through the element: reference-type property returns the same
            // reference — allow. Value-type property returns a copy — allow only
            // when the remaining path reaches nested reference storage (array /
            // class field) before the by-ref target; otherwise refuse.
            var elementType = semanticModel.GetTypeInfo(memberAccess).Type;
            if (elementType is { IsReferenceType: true })
                continue;

            if (elementType is { IsValueType: true } &&
                !ValueTypeElementByRefIsUnsafe(memberAccess, coreTarget, semanticModel))
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

    private static ExpressionSyntax UnwrapParenthesesAndNullForgiving(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = postfix.Operand;
                    continue;
                default:
                    return expression;
            }
        }
    }

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
            // Larger aggregate (e.g. wrapped in (point, X: 5)) — not the
            // converted storage itself; element checks must use
            // ExpressionYieldsConvertedStorage on the carrier member.
            return false;
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

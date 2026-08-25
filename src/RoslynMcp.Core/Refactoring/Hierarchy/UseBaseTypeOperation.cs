using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Replaces selected derived-type references with a compatible base type or
/// interface where every used member exists on that base.
/// </summary>
public sealed class UseBaseTypeOperation : RefactoringOperationBase<UseBaseTypeParams>
{
    /// <summary>
    /// Creates a new use-base-type operation.
    /// </summary>
    public UseBaseTypeOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(UseBaseTypeParams @params) => Validate(@params);

    /// <summary>
    /// Validates use-base-type parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(UseBaseTypeParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        UseBaseTypeParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var derivedDecl = FindTypeDeclaration(root, semanticModel, @params.TypeName, cancellationToken);
        if (derivedDecl == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        var derivedSymbol = semanticModel.GetDeclaredSymbol(derivedDecl, cancellationToken) as INamedTypeSymbol;
        if (derivedSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        var target = GetTargetBaseType(derivedSymbol, @params.TargetBaseType);
        var candidates = await FindTypeReferencesAsync(derivedSymbol, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoEligibleReferences,
                $"No eligible type references of '{derivedSymbol.Name}' can be rewritten to '{target.Name}'.");
        }

        var rewrites = new List<RewritableReference>();
        var sawIncompatibleMembers = false;

        foreach (var candidate in candidates)
        {
            ValidateDocumentIsEditable(candidate.Document, Context.Workspace);

            if (!await CanUseBaseTypeAsync(candidate, derivedSymbol, target, cancellationToken))
            {
                sawIncompatibleMembers = true;
                continue;
            }

            var constructed = GetConstructedTarget(target, candidate.TypeSyntax, candidate.SemanticModel);
            rewrites.Add(new RewritableReference(
                candidate.Document,
                candidate.TypeSyntax,
                CreateBaseTypeReference(constructed, candidate.TypeSyntax, candidate.SemanticModel),
                constructed));
        }

        if (rewrites.Count == 0)
        {
            throw new RefactoringException(
                sawIncompatibleMembers
                    ? ErrorCodes.BaseCannotSatisfyUsedMembers
                    : ErrorCodes.NoEligibleReferences,
                sawIncompatibleMembers
                    ? $"Base type '{target.Name}' cannot satisfy members used through '{derivedSymbol.Name}'."
                    : $"No eligible type references of '{derivedSymbol.Name}' can be rewritten to '{target.Name}'.");
        }

        if (@params.Preview)
            return CreatePreviewResult(operationId, rewrites, derivedSymbol, target);

        var solution = await ApplyRewritesAsync(rewrites, cancellationToken);
        var commitResult = await CommitChangesAsync(solution, cancellationToken);

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
                Name = target.Name,
                FullyQualifiedName = target.ToDisplayString(),
                Kind = target.TypeKind == TypeKind.Interface
                    ? Contracts.Enums.SymbolKind.Interface
                    : Contracts.Enums.SymbolKind.Class
            },
            rewrites.Count,
            0);
    }

    private static TypeDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        SemanticModel model,
        string typeName,
        CancellationToken cancellationToken)
    {
        var simpleName = typeName.Contains('.')
            ? typeName[(typeName.LastIndexOf('.') + 1)..]
            : typeName;

        var matches = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(type => type.Identifier.Text == simpleName)
            .ToList();

        if (matches.Count == 0)
            return null;

        if (!typeName.Contains('.') || matches.Count == 1)
            return matches[0];

        foreach (var match in matches)
        {
            if (model.GetDeclaredSymbol(match, cancellationToken) is not INamedTypeSymbol symbol)
                continue;

            if (TypeNameMatches(symbol, typeName))
                return match;
        }

        return null;
    }

    internal static bool TypeNameMatches(INamedTypeSymbol symbol, string typeName)
    {
        if (symbol.Name.Equals(typeName, StringComparison.Ordinal) ||
            symbol.ToDisplayString().Equals(typeName, StringComparison.Ordinal) ||
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Equals(typeName, StringComparison.Ordinal))
        {
            return true;
        }

        var metadata = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        if (metadata.Equals(typeName, StringComparison.Ordinal))
            return true;

        var containing = symbol.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrWhiteSpace(containing) || symbol.ContainingNamespace!.IsGlobalNamespace)
            return false;

        return $"{containing}.{symbol.Name}".Equals(typeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the base class or interface that references should use.
    /// </summary>
    internal static INamedTypeSymbol GetTargetBaseType(INamedTypeSymbol derived, string? targetTypeName)
    {
        var classBases = new List<INamedTypeSymbol>();
        for (var baseType = derived.BaseType; baseType != null; baseType = baseType.BaseType)
            classBases.Add(baseType);

        var interfaces = derived.AllInterfaces.ToList();

        if (string.IsNullOrWhiteSpace(targetTypeName))
        {
            var meaningful = classBases
                .Where(candidate => candidate.SpecialType != SpecialType.System_Object)
                .ToList();
            if (meaningful.Count > 0)
                return meaningful[0];

            if (derived.Interfaces.Length == 1)
                return derived.Interfaces[0];

            if (derived.Interfaces.Length > 1)
            {
                throw new RefactoringException(
                    ErrorCodes.NoCommonBase,
                    $"Type '{derived.Name}' implements multiple interfaces; specify targetBaseType.");
            }

            throw new RefactoringException(
                ErrorCodes.NoCommonBase,
                $"Type '{derived.Name}' has no base class or interface to use.");
        }

        var match = classBases.Concat(interfaces).FirstOrDefault(candidate =>
            candidate.Name.Equals(targetTypeName, StringComparison.Ordinal) ||
            candidate.ToDisplayString().Equals(targetTypeName, StringComparison.Ordinal));

        if (match == null)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotFound,
                $"Type '{targetTypeName}' is not a base class or interface of '{derived.Name}'.");
        }

        return match;
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

    private async Task<List<TypeReference>> FindTypeReferencesAsync(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        var search = await ReferenceTracker.FindAllReferencesAsync(type, cancellationToken);
        var results = new List<TypeReference>();
        var seen = new HashSet<(DocumentId Id, int Start, int End)>();

        foreach (var (documentId, locations) in search.ReferencesByDocument)
        {
            var document = Context.Solution.GetDocument(documentId);
            if (document == null)
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                continue;

            foreach (var location in locations)
            {
                if (location.IsImplicit || !location.Location.IsInSource)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var typeSyntax = GetReplaceableTypeSyntax(node);
                if (typeSyntax == null || !IsRewritableAnnotation(typeSyntax))
                    continue;

                var key = (document.Id, typeSyntax.Span.Start, typeSyntax.Span.End);
                if (!seen.Add(key))
                    continue;

                results.Add(new TypeReference(document, typeSyntax, model));
            }
        }

        return results;
    }

    internal static TypeSyntax? GetReplaceableTypeSyntax(SyntaxNode node)
    {
        if (node is IdentifierNameSyntax identifier &&
            identifier.Parent is QualifiedNameSyntax qualified &&
            qualified.Right == identifier)
        {
            var current = qualified;
            while (current.Parent is QualifiedNameSyntax parent)
                current = parent;
            return current;
        }

        if (node is TypeSyntax typeSyntax)
            return typeSyntax;

        return node.FirstAncestorOrSelf<TypeSyntax>();
    }

    internal static bool IsRewritableAnnotation(TypeSyntax type)
    {
        if (type.Parent is ArrayTypeSyntax or PointerTypeSyntax)
            return false;

        var outermost = GetOutermostTypeSyntax(type);
        var parent = outermost.Parent;

        switch (parent)
        {
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
            case TypeOfExpressionSyntax:
            case SizeOfExpressionSyntax:
            case DefaultExpressionSyntax:
            case CastExpressionSyntax:
            case BaseListSyntax:
            case SimpleBaseTypeSyntax:
            case PrimaryConstructorBaseTypeSyntax:
            case TypeArgumentListSyntax:
            case TypeParameterConstraintClauseSyntax:
            case TypeConstraintSyntax:
            case AttributeSyntax:
            case AttributeArgumentSyntax:
            case DeclarationPatternSyntax:
            case TypePatternSyntax:
            case RecursivePatternSyntax:
            case ConstantPatternSyntax:
            case OperatorDeclarationSyntax:
            case ConversionOperatorDeclarationSyntax:
                return false;
            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression):
                return false;
            case VariableDeclarationSyntax:
            case ParameterSyntax:
            case MethodDeclarationSyntax:
            case LocalFunctionStatementSyntax:
            case PropertyDeclarationSyntax:
            case EventDeclarationSyntax:
            case IndexerDeclarationSyntax:
            case DelegateDeclarationSyntax:
            case ForEachStatementSyntax:
                return true;
            default:
                return false;
        }
    }

    private static TypeSyntax GetOutermostTypeSyntax(TypeSyntax type)
    {
        var current = type;
        while (current.Parent is NullableTypeSyntax or RefTypeSyntax)
            current = (TypeSyntax)current.Parent;
        return current;
    }

    /// <summary>
    /// True when <paramref name="usage"/> is the chosen base or a type that
    /// inherits or implements it.
    /// </summary>
    internal static bool CanUseBaseType(ITypeSymbol usage, ITypeSymbol baseType)
    {
        if (usage.TypeKind == TypeKind.Error || baseType.TypeKind == TypeKind.Error)
            return false;

        return IsSameOrDerived(usage, baseType);
    }

    private async Task<bool> CanUseBaseTypeAsync(
        TypeReference candidate,
        INamedTypeSymbol derived,
        INamedTypeSymbol baseType,
        CancellationToken cancellationToken)
    {
        if (!CanUseBaseType(derived, baseType))
            return false;

        if (HasTargetTypedNew(candidate.TypeSyntax, candidate.SemanticModel, derived))
            return false;

        var declared = GetDeclaredSymbols(candidate.TypeSyntax, candidate.SemanticModel, cancellationToken);
        if (declared.Count == 0)
            return false;

        foreach (var symbol in declared)
        {
            if (IsSignatureLocked(symbol, candidate.TypeSyntax))
                return false;

            if (!await UsagesCompatibleWithBaseAsync(symbol, candidate, derived, baseType, cancellationToken))
                return false;
        }

        return true;
    }

    private static bool HasTargetTypedNew(
        TypeSyntax annotation,
        SemanticModel model,
        INamedTypeSymbol derived)
    {
        var outermost = GetOutermostTypeSyntax(annotation);
        foreach (var creation in EnumerateInferredCreations(outermost.Parent))
        {
            var converted = model.GetTypeInfo(creation).ConvertedType;
            if (converted != null && IsSameOrConstructed(converted, derived))
                return true;
        }

        return false;
    }

    private static IEnumerable<ImplicitObjectCreationExpressionSyntax> EnumerateInferredCreations(SyntaxNode? parent)
    {
        switch (parent)
        {
            case VariableDeclarationSyntax variables:
                foreach (var variable in variables.Variables)
                {
                    if (variable.Initializer?.Value is ImplicitObjectCreationExpressionSyntax creation)
                        yield return creation;
                }

                break;
            case PropertyDeclarationSyntax property:
                if (property.Initializer?.Value is ImplicitObjectCreationExpressionSyntax propertyCreation)
                    yield return propertyCreation;
                foreach (var creation in EnumerateReturnCreations(property.ExpressionBody, GetAccessorBodies(property)))
                    yield return creation;
                break;
            case MethodDeclarationSyntax method:
                foreach (var creation in EnumerateReturnCreations(method.ExpressionBody, method.Body))
                    yield return creation;
                break;
            case LocalFunctionStatementSyntax localFunction:
                foreach (var creation in EnumerateReturnCreations(localFunction.ExpressionBody, localFunction.Body))
                    yield return creation;
                break;
        }
    }

    private static IEnumerable<BlockSyntax?> GetAccessorBodies(PropertyDeclarationSyntax property)
    {
        if (property.AccessorList == null)
            yield break;

        foreach (var accessor in property.AccessorList.Accessors)
            yield return accessor.Body;
    }

    private static IEnumerable<ImplicitObjectCreationExpressionSyntax> EnumerateReturnCreations(
        ArrowExpressionClauseSyntax? expressionBody,
        BlockSyntax? body)
    {
        if (expressionBody != null)
        {
            foreach (var creation in expressionBody.DescendantNodesAndSelf().OfType<ImplicitObjectCreationExpressionSyntax>())
                yield return creation;
        }

        if (body == null)
            yield break;

        foreach (var ret in body.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (ret.Expression == null)
                continue;

            foreach (var creation in ret.Expression.DescendantNodesAndSelf().OfType<ImplicitObjectCreationExpressionSyntax>())
                yield return creation;
        }
    }

    private static IEnumerable<ImplicitObjectCreationExpressionSyntax> EnumerateReturnCreations(
        ArrowExpressionClauseSyntax? expressionBody,
        IEnumerable<BlockSyntax?> bodies)
    {
        foreach (var creation in EnumerateReturnCreations(expressionBody, (BlockSyntax?)null))
            yield return creation;

        foreach (var body in bodies)
        {
            foreach (var creation in EnumerateReturnCreations(null, body))
                yield return creation;
        }
    }

    private static List<ISymbol> GetDeclaredSymbols(
        TypeSyntax type,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var symbols = new List<ISymbol>();
        var parent = GetOutermostTypeSyntax(type).Parent;

        switch (parent)
        {
            case VariableDeclarationSyntax variables:
                foreach (var variable in variables.Variables)
                {
                    var symbol = model.GetDeclaredSymbol(variable, cancellationToken);
                    if (symbol != null)
                        symbols.Add(symbol);
                }

                break;
            case ParameterSyntax parameter:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(parameter, cancellationToken));
                break;
            case MethodDeclarationSyntax method:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(method, cancellationToken));
                break;
            case LocalFunctionStatementSyntax localFunction:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(localFunction, cancellationToken));
                break;
            case PropertyDeclarationSyntax property:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(property, cancellationToken));
                break;
            case EventDeclarationSyntax @event:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(@event, cancellationToken));
                break;
            case IndexerDeclarationSyntax indexer:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(indexer, cancellationToken));
                break;
            case ForEachStatementSyntax forEach:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(forEach, cancellationToken));
                break;
            case DelegateDeclarationSyntax @delegate:
                AddIfNotNull(symbols, model.GetDeclaredSymbol(@delegate, cancellationToken));
                break;
        }

        return symbols;
    }

    private static void AddIfNotNull(List<ISymbol> symbols, ISymbol? symbol)
    {
        if (symbol != null)
            symbols.Add(symbol);
    }

    private static bool IsSignatureLocked(ISymbol symbol, TypeSyntax annotation)
    {
        var method = symbol as IMethodSymbol
            ?? (symbol as IParameterSymbol)?.ContainingSymbol as IMethodSymbol;
        var property = symbol as IPropertySymbol
            ?? (symbol as IParameterSymbol)?.ContainingSymbol as IPropertySymbol;
        if (method == null && property == null)
            return false;

        var outermost = GetOutermostTypeSyntax(annotation);
        var changesSignature = outermost.Parent is MethodDeclarationSyntax
            or LocalFunctionStatementSyntax
            or ParameterSyntax
            or PropertyDeclarationSyntax
            or IndexerDeclarationSyntax
            or DelegateDeclarationSyntax;

        if (!changesSignature)
            return false;

        if (method != null)
        {
            if (method.IsOverride || method.IsAbstract || method.ExplicitInterfaceImplementations.Length > 0)
                return true;

            return ImplementsInterfaceMember(method);
        }

        if (property!.IsOverride || property.IsAbstract || property.ExplicitInterfaceImplementations.Length > 0)
            return true;

        return ImplementsInterfaceMember(property);
    }

    private static bool ImplementsInterfaceMember(ISymbol symbol)
    {
        var containing = symbol.ContainingType;
        if (containing == null)
            return false;

        foreach (var iface in containing.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(symbol.Name))
            {
                var implementation = containing.FindImplementationForInterfaceMember(member);
                if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, symbol))
                    return true;
            }
        }

        return false;
    }

    private async Task<bool> UsagesCompatibleWithBaseAsync(
        ISymbol symbol,
        TypeReference candidate,
        INamedTypeSymbol derived,
        INamedTypeSymbol baseType,
        CancellationToken cancellationToken)
    {
        if (symbol is IMethodSymbol method &&
            GetOutermostTypeSyntax(candidate.TypeSyntax).Parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax)
        {
            return await AnalyzeMethodReturnUsagesAsync(method, derived, baseType, cancellationToken);
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, Context.Solution, cancellationToken);
        foreach (var referenced in references)
        {
            foreach (var location in referenced.Locations)
            {
                if (location.IsImplicit || !location.Location.IsInSource)
                    continue;

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (!UsageCompatible(node, derived, baseType, model))
                    return false;
            }
        }

        return true;
    }

    private async Task<bool> AnalyzeMethodReturnUsagesAsync(
        IMethodSymbol method,
        INamedTypeSymbol derived,
        INamedTypeSymbol baseType,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(method, Context.Solution, cancellationToken);
        foreach (var referenced in references)
        {
            foreach (var location in referenced.Locations)
            {
                if (location.IsImplicit || !location.Location.IsInSource)
                    continue;

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                if (invocation == null)
                    continue;

                if (!UsageCompatible(invocation, derived, baseType, model))
                    return false;
            }
        }

        return true;
    }

    internal static bool UsageCompatible(
        SyntaxNode node,
        INamedTypeSymbol derived,
        INamedTypeSymbol baseType,
        SemanticModel model)
    {
        var expression = node as ExpressionSyntax ?? node.FirstAncestorOrSelf<ExpressionSyntax>();
        if (expression == null)
            return true;

        if (expression.Parent is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Expression == expression)
        {
            var member = model.GetSymbolInfo(memberAccess).Symbol ?? model.GetSymbolInfo(memberAccess.Name).Symbol;
            return member != null && IsAvailableOn(member, baseType);
        }

        if (expression.Parent is ConditionalAccessExpressionSyntax conditional &&
            conditional.Expression == expression)
        {
            var member = conditional.WhenNotNull switch
            {
                MemberBindingExpressionSyntax binding => model.GetSymbolInfo(binding).Symbol,
                ElementBindingExpressionSyntax element => model.GetSymbolInfo(element).Symbol,
                _ => model.GetSymbolInfo(conditional.WhenNotNull).Symbol
            };
            return member != null && IsAvailableOn(member, baseType);
        }

        if (expression.Parent is ElementAccessExpressionSyntax elementAccess &&
            elementAccess.Expression == expression)
        {
            var member = model.GetSymbolInfo(elementAccess).Symbol;
            return member != null && IsAvailableOn(member, baseType);
        }

        if (expression.Parent is VariableDeclaratorSyntax or ParameterSyntax or PropertyDeclarationSyntax)
            return true;

        if (expression.Parent is ExpressionStatementSyntax)
            return true;

        if (IsNullComparison(expression) || IsNameOfArgument(expression))
            return true;

        if (expression.Parent is EqualsValueClauseSyntax equals &&
            equals.Parent is VariableDeclaratorSyntax declarator &&
            declarator.Parent is VariableDeclarationSyntax declaration &&
            declaration.Type.IsVar)
        {
            return VarUsagesCompatible(declarator, derived, baseType, model);
        }

        var typeInfo = model.GetTypeInfo(expression);
        if (typeInfo.ConvertedType == null)
            return true;

        if (SymbolEqualityComparer.Default.Equals(typeInfo.Type, typeInfo.ConvertedType) &&
            IsSameOrConstructed(typeInfo.ConvertedType, derived))
        {
            return false;
        }

        var conversion = model.Compilation.ClassifyConversion(baseType, typeInfo.ConvertedType);
        return conversion.IsImplicit;
    }

    private static bool VarUsagesCompatible(
        VariableDeclaratorSyntax declarator,
        INamedTypeSymbol derived,
        INamedTypeSymbol baseType,
        SemanticModel model)
    {
        var local = model.GetDeclaredSymbol(declarator);
        if (local == null)
            return false;

        var root = declarator.SyntaxTree.GetRoot();
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Span == declarator.Identifier.Span)
                continue;

            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol == null || !SymbolEqualityComparer.Default.Equals(symbol, local))
                continue;

            if (!UsageCompatible(identifier, derived, baseType, model))
                return false;
        }

        return true;
    }

    private static bool IsNullComparison(ExpressionSyntax expression)
    {
        if (expression.Parent is BinaryExpressionSyntax binary &&
            (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression)))
        {
            var other = binary.Left == expression ? binary.Right : binary.Left;
            return other.IsKind(SyntaxKind.NullLiteralExpression);
        }

        if (expression.Parent is IsPatternExpressionSyntax pattern && pattern.Expression == expression)
            return IsNullPattern(pattern.Pattern);

        return false;
    }

    private static bool IsNullPattern(PatternSyntax pattern)
    {
        if (pattern is ConstantPatternSyntax constant &&
            constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return true;
        }

        return pattern is UnaryPatternSyntax unary && IsNullPattern(unary.Pattern);
    }

    private static bool IsNameOfArgument(ExpressionSyntax expression)
    {
        return expression.Parent is ArgumentSyntax argument &&
               argument.Parent?.Parent is InvocationExpressionSyntax invocation &&
               invocation.Expression is IdentifierNameSyntax name &&
               name.Identifier.Text == "nameof";
    }

    internal static bool IsAvailableOn(ISymbol member, INamedTypeSymbol baseType)
    {
        if (member is IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
            return false;

        if (IsDeclaredOnHierarchy(member.ContainingType, baseType))
            return true;

        switch (member)
        {
            case IMethodSymbol method:
                if (method.OverriddenMethod != null && IsAvailableOn(method.OverriddenMethod, baseType))
                    return true;

                foreach (var implemented in method.ExplicitInterfaceImplementations)
                {
                    if (IsAvailableOn(implemented, baseType))
                        return true;
                }

                return ImplementsBaseInterfaceMember(method, baseType);

            case IPropertySymbol property:
                if (property.OverriddenProperty != null && IsAvailableOn(property.OverriddenProperty, baseType))
                    return true;

                foreach (var implemented in property.ExplicitInterfaceImplementations)
                {
                    if (IsAvailableOn(implemented, baseType))
                        return true;
                }

                return ImplementsBaseInterfaceMember(property, baseType);

            case IEventSymbol @event:
                if (@event.OverriddenEvent != null && IsAvailableOn(@event.OverriddenEvent, baseType))
                    return true;

                foreach (var implemented in @event.ExplicitInterfaceImplementations)
                {
                    if (IsAvailableOn(implemented, baseType))
                        return true;
                }

                return ImplementsBaseInterfaceMember(@event, baseType);
        }

        return false;
    }

    private static bool IsDeclaredOnHierarchy(INamedTypeSymbol? container, INamedTypeSymbol baseType)
    {
        if (container == null)
            return false;

        if (container.SpecialType == SpecialType.System_Object)
            return true;

        if (SymbolEqualityComparer.Default.Equals(container, baseType) ||
            SymbolEqualityComparer.Default.Equals(container.OriginalDefinition, baseType.OriginalDefinition))
        {
            return true;
        }

        if (baseType.AllInterfaces.Any(iface =>
                SymbolEqualityComparer.Default.Equals(iface, container) ||
                SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, container.OriginalDefinition)))
        {
            return true;
        }

        for (var current = baseType.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, container) ||
                SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, container.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsBaseInterfaceMember(ISymbol member, INamedTypeSymbol baseType)
    {
        var containing = member.ContainingType;
        if (containing == null)
            return false;

        foreach (var iface in containing.AllInterfaces)
        {
            if (!IsDeclaredOnHierarchy(iface, baseType) &&
                !SymbolEqualityComparer.Default.Equals(iface, baseType))
            {
                continue;
            }

            foreach (var ifaceMember in iface.GetMembers(member.Name))
            {
                var implementation = containing.FindImplementationForInterfaceMember(ifaceMember);
                if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, member))
                    return true;
            }
        }

        return false;
    }

    private static bool IsSameOrConstructed(ITypeSymbol type, INamedTypeSymbol derived)
    {
        return SymbolEqualityComparer.Default.Equals(type, derived) ||
               SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, derived.OriginalDefinition);
    }

    private static bool IsSameOrDerived(ITypeSymbol usage, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(usage, baseType))
            return true;

        if (usage is not INamedTypeSymbol named)
            return false;

        for (var current = named.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }

        return named.AllInterfaces.Any(iface => SymbolEqualityComparer.Default.Equals(iface, baseType));
    }

    internal static INamedTypeSymbol GetConstructedTarget(
        INamedTypeSymbol target,
        TypeSyntax original,
        SemanticModel model)
    {
        var referenced = model.GetTypeInfo(original).Type as INamedTypeSymbol
            ?? model.GetSymbolInfo(original).Symbol as INamedTypeSymbol;
        if (referenced == null)
            return target;

        if (target.TypeKind == TypeKind.Interface)
        {
            return referenced.AllInterfaces.FirstOrDefault(iface =>
                SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, target.OriginalDefinition))
                ?? target;
        }

        for (var current = referenced.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, target.OriginalDefinition))
                return current;
        }

        return target;
    }

    internal static TypeSyntax CreateBaseTypeReference(
        INamedTypeSymbol baseType,
        TypeSyntax original,
        SemanticModel model)
    {
        var display = baseType.ToMinimalDisplayString(model, original.SpanStart);
        if (string.IsNullOrWhiteSpace(display) ||
            NameBindsToDifferentType(display, baseType, model, original.SpanStart))
        {
            display = baseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        }

        return SyntaxFactory.ParseTypeName(display)
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());
    }

    private static bool NameBindsToDifferentType(
        string display,
        INamedTypeSymbol expected,
        SemanticModel model,
        int position)
    {
        var parsed = SyntaxFactory.ParseTypeName(display);
        var spec = model.GetSpeculativeTypeInfo(position, parsed, SpeculativeBindingOption.BindAsTypeOrNamespace);
        if (spec.Type is not INamedTypeSymbol bound)
            return true;

        return !SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, expected.OriginalDefinition) &&
               !SymbolEqualityComparer.Default.Equals(bound, expected);
    }

    private static async Task<Solution> ApplyRewritesAsync(
        IReadOnlyList<RewritableReference> rewrites,
        CancellationToken cancellationToken)
    {
        var solution = rewrites[0].Document.Project.Solution;

        foreach (var group in rewrites.GroupBy(rewrite => rewrite.Document.Id))
        {
            var document = solution.GetDocument(group.Key);
            if (document == null)
                throw new RefactoringException(ErrorCodes.RoslynError, "Could not locate document to update.");

            ValidateDocumentIsEditable(document, document.Project.Solution.Workspace);

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var map = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var rewrite in group)
            {
                var current = root.FindNode(rewrite.Original.Span, getInnermostNodeForTie: true);
                var type = GetReplaceableTypeSyntax(current) ?? rewrite.Original;
                map[type] = rewrite.Replacement.WithTriviaFrom(type);
            }

            var newRoot = root.ReplaceNodes(map.Keys, (original, _) => map[original]);
            if (newRoot is CompilationUnitSyntax unit &&
                group.Any(rewrite => rewrite.Replacement is IdentifierNameSyntax))
            {
                newRoot = EnsureUsing(unit, group.First().BaseType);
            }

            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        return solution;
    }

    private static CompilationUnitSyntax EnsureUsing(CompilationUnitSyntax root, INamedTypeSymbol baseType)
    {
        var namespaceName = baseType.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrWhiteSpace(namespaceName) || baseType.ContainingNamespace!.IsGlobalNamespace)
            return root;

        if (root.Usings.Any(directive => directive.Name?.ToString() == namespaceName))
            return root;

        var fileScoped = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScoped?.Name.ToString() == namespaceName)
            return root;

        var blockScoped = root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (blockScoped?.Name.ToString() == namespaceName)
            return root;

        var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName))
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        return root.AddUsings(usingDirective);
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        IReadOnlyList<RewritableReference> rewrites,
        INamedTypeSymbol derived,
        INamedTypeSymbol baseType)
    {
        var pendingChanges = rewrites
            .GroupBy(rewrite => rewrite.Document.FilePath ?? rewrite.Document.Name)
            .Select(group => new PendingChange
            {
                File = group.Key,
                ChangeType = ChangeKind.Modify,
                Description = $"Replace {derived.Name} with {baseType.Name} in {group.Count()} type reference(s)",
                BeforeSnippet = string.Join(", ", group.Select(item => item.Original.ToString()).Distinct()),
                AfterSnippet = string.Join(", ", group.Select(item => item.Replacement.ToString()).Distinct())
            })
            .ToList();

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record TypeReference(Document Document, TypeSyntax TypeSyntax, SemanticModel SemanticModel);

    private sealed record RewritableReference(
        Document Document,
        TypeSyntax Original,
        TypeSyntax Replacement,
        INamedTypeSymbol BaseType);
}

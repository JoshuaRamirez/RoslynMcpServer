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

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates interface member implementations for a type.
/// Honors optional <c>line</c> to disambiguate same-named types in one
/// file (identifier preferred, then smallest containing type). Honors
/// <c>replaceExisting</c> to include already-implemented interface
/// members, remove those declarations (including across partials) by
/// signature, and insert a standard generated stub. Property/event accessors
/// are never emitted as ordinary methods. Extra modifiers on the old
/// implementation are not copied.
/// </summary>
public sealed class ImplementInterfaceOperation : RefactoringOperationBase<ImplementInterfaceParams>
{
    /// <summary>
    /// Marks the type declaration selected before
    /// <see cref="RemoveExistingImplementationsAcrossPartialsAsync"/> so the
    /// same node can be recovered after a same-file earlier partial shrinks
    /// and shifts both <c>SpanStart</c> and physical line.
    /// </summary>
    private static readonly SyntaxAnnotation TargetTypeAnnotation = new("implement-interface-target-type");

    /// <summary>
    /// Creates a new implement interface operation.
    /// </summary>
    public ImplementInterfaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ImplementInterfaceParams @params) => Validate(@params);

    /// <summary>
    /// Validates implement-interface parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ImplementInterfaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.InterfaceName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "interfaceName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ImplementInterfaceParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find the type declaration (optional line disambiguates same-named types)
        var typeDeclaration = FindTypeDeclaration(root, @params.TypeName, @params.Line);

        if (typeDeclaration == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        // Find the interface
        var interfaceSymbol = await FindInterfaceAsync(
            typeSymbol,
            @params.InterfaceName,
            cancellationToken);

        if (interfaceSymbol == null)
        {
            throw new RefactoringException(
                ErrorCodes.InterfaceNotFound,
                $"Interface '{@params.InterfaceName}' not found.");
        }

        // Missing implementable members, plus already-implemented ones when
        // replaceExisting is true. Skip property/event accessors — those
        // are implemented as part of the property / indexer / event stub
        // (emitting get_Item / set_Item as ordinary methods is CS0111).
        var eligibleMembers = CollectMembersToImplement(
            typeSymbol,
            interfaceSymbol,
            @params.ReplaceExisting);

        // Filter to requested members if specified. Indexers match metadata
        // name ("Item"), Roslyn name ("this[]"), and conventional display
        // ("this[int i]") so callers can filter by any of those forms.
        if (@params.Members != null && @params.Members.Count > 0)
        {
            var requestedSet = new HashSet<string>(@params.Members);
            eligibleMembers = eligibleMembers.Where(m => MatchesRequestedMember(m, requestedSet)).ToList();
        }

        if (eligibleMembers.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberAlreadyImplemented,
                "All interface members are already implemented.");
        }

        var replacements = ResolveReplacements(
            typeSymbol,
            eligibleMembers,
            @params.ReplaceExisting,
            @params.ExplicitImplementation);
        var membersToReplace = eligibleMembers.Where(m => replacements.ContainsKey(m)).ToList();
        var membersToGenerate = eligibleMembers.Where(m => !replacements.ContainsKey(m)).ToList();

        // Generate implementations
        var implementations = GenerateImplementations(
            eligibleMembers,
            @params.ExplicitImplementation,
            @params.ThrowNotImplemented);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                membersToGenerate,
                membersToReplace,
                implementations,
                replacements,
                document.Project.Solution,
                cancellationToken);
        }

        var solution = document.Project.Solution;
        if (replacements.Count > 0)
        {
            // Annotate before the rewrite. Removing a member from an
            // earlier same-file partial shifts both SpanStart and the
            // physical line of a later selected partial — do not re-find
            // with those stale values.
            root = root.ReplaceNode(
                typeDeclaration,
                typeDeclaration.WithAdditionalAnnotations(TargetTypeAnnotation));
            document = document.WithSyntaxRoot(root);
            solution = document.Project.Solution;

            solution = await RemoveExistingImplementationsAcrossPartialsAsync(
                solution, typeSymbol, replacements.Values, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{@params.TypeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            typeDeclaration = root.GetAnnotatedNodes(TargetTypeAnnotation)
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault()
                ?? throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"Type '{@params.TypeName}' not found in file.");
        }

        // Add implementations to type
        var newTypeDeclaration = AddMembers(typeDeclaration, implementations);
        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);

        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = newDocument.Project.Solution;

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
                Name = @params.TypeName,
                FullyQualifiedName = typeSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private async Task<INamedTypeSymbol?> FindInterfaceAsync(
        INamedTypeSymbol typeSymbol,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        // First check if type already implements/references the interface
        var directInterface = typeSymbol.AllInterfaces
            .FirstOrDefault(i =>
                i.Name == interfaceName ||
                i.ToDisplayString() == interfaceName);

        if (directInterface != null)
        {
            return directInterface;
        }

        // Try to find interface in workspace
        return await TypeResolver.FindTypeByNameAsync(interfaceName, cancellationToken);
    }

    /// <summary>
    /// Missing implementable interface members (today's
    /// <see cref="MemberAnalyzer.GetUnimplementedMembers"/> plus
    /// <see cref="IsImplementableInterfaceMember"/>). When
    /// <paramref name="replaceExisting"/> is true, already-implemented
    /// members declared on this type that this tool would emit are added.
    /// </summary>
    internal static List<ISymbol> CollectMembersToImplement(
        INamedTypeSymbol typeSymbol,
        INamedTypeSymbol interfaceSymbol,
        bool replaceExisting)
    {
        var result = new List<ISymbol>();

        foreach (var member in MemberAnalyzer.GetUnimplementedMembers(typeSymbol, interfaceSymbol))
        {
            if (IsImplementableInterfaceMember(member))
                AddUnique(result, member);
        }

        if (!replaceExisting)
            return result;

        foreach (var member in interfaceSymbol.GetMembers())
        {
            if (!IsImplementableInterfaceMember(member))
                continue;

            var implementation = typeSymbol.FindImplementationForInterfaceMember(member);
            if (implementation == null)
                continue;
            if (!IsDeclaredOnType(typeSymbol, implementation))
                continue;

            AddUnique(result, member);
        }

        return result;
    }

    private static void AddUnique(List<ISymbol> members, ISymbol member)
    {
        if (members.Any(existing => SignaturesMatch(existing, member)))
            return;

        members.Add(member);
    }

    private static bool IsDeclaredOnType(INamedTypeSymbol typeSymbol, ISymbol implementation) =>
        SymbolEqualityComparer.Default.Equals(implementation.ContainingType, typeSymbol);

    /// <summary>
    /// Maps each selected interface member to the existing implementation
    /// on this type that will be removed. Exact signature match wins. Two
    /// same-name existing implementations with no exact match is
    /// <see cref="ErrorCodes.NameCollision"/>.
    /// </summary>
    internal static Dictionary<ISymbol, ISymbol> ResolveReplacements(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<ISymbol> selectedMembers,
        bool replaceExisting,
        bool explicitImplementation)
    {
        var replacements = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
        if (!replaceExisting)
            return replacements;

        foreach (var selected in selectedMembers)
        {
            var existing = FindExistingImplementation(
                typeSymbol, selected, explicitImplementation, out var ambiguous);
            if (ambiguous)
            {
                throw new RefactoringException(
                    ErrorCodes.NameCollision,
                    $"Multiple existing implementations named '{selected.Name}' and none matches the selected signature.");
            }

            if (existing != null)
                replacements[selected] = existing;
        }

        return replacements;
    }

    private static ISymbol? FindExistingImplementation(
        INamedTypeSymbol typeSymbol,
        ISymbol selected,
        bool explicitImplementation,
        out bool ambiguous)
    {
        ambiguous = false;
        var sameName = new List<ISymbol>();
        ISymbol? exact = null;

        foreach (var member in typeSymbol.GetMembers())
        {
            if (!IsEligibleExistingImplementation(member, explicitImplementation))
                continue;
            if (!NamesMatch(member, selected))
                continue;
            // Explicit IA.M and IB.M share an unqualified name + signature.
            // Only the implementation that actually implements the selected
            // interface member is an exact match — otherwise replacing IA.M
            // can delete IB.M and emit a second IA.M.
            if (IsExplicitImplementation(member) && !ExplicitlyImplementsSelected(member, selected))
                continue;

            sameName.Add(member);
            if (SignaturesMatch(member, selected))
                exact = member;
        }

        if (exact != null)
            return exact;

        if (sameName.Count >= 2)
        {
            ambiguous = true;
            return null;
        }

        return null;
    }

    private static bool IsEligibleExistingImplementation(ISymbol member, bool explicitImplementation)
    {
        if (member.IsImplicitlyDeclared)
            return false;
        if (!MatchesRequestedForm(member, explicitImplementation))
            return false;

        return member switch
        {
            IMethodSymbol method => method.MethodKind is MethodKind.Ordinary
                or MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
    }

    private static bool MatchesRequestedForm(ISymbol member, bool explicitImplementation)
    {
        var isExplicit = IsExplicitImplementation(member);
        return explicitImplementation ? isExplicit : !isExplicit;
    }

    private static bool IsExplicitImplementation(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0
                || method.MethodKind == MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };

    /// <summary>
    /// True when <paramref name="existing"/> is an explicit implementation of
    /// <paramref name="selected"/> (the interface member), not merely another
    /// interface's same-signature member.
    /// </summary>
    private static bool ExplicitlyImplementsSelected(ISymbol existing, ISymbol selected)
    {
        foreach (var implemented in GetExplicitImplementations(existing))
        {
            if (SymbolEqualityComparer.Default.Equals(implemented, selected))
                return true;
        }

        return false;
    }

    private static IEnumerable<ISymbol> GetExplicitImplementations(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations,
            _ => []
        };

    private static bool NamesMatch(ISymbol left, ISymbol right)
    {
        if (string.Equals(UnqualifiedName(left), UnqualifiedName(right), StringComparison.Ordinal))
            return true;

        if (ExplicitlyImplementsName(left, UnqualifiedName(right))
            || ExplicitlyImplementsName(right, UnqualifiedName(left)))
        {
            return true;
        }

        return left is IPropertySymbol { IsIndexer: true }
            && right is IPropertySymbol { IsIndexer: true }
            && (string.Equals(left.MetadataName, right.MetadataName, StringComparison.Ordinal)
                || string.Equals(left.MetadataName, "Item", StringComparison.Ordinal)
                || string.Equals(right.MetadataName, "Item", StringComparison.Ordinal));
    }

    /// <summary>
    /// Explicit interface implementations may report <c>IFoo.M</c> as
    /// <see cref="ISymbol.Name"/>; strip the interface qualifier so they
    /// still match the interface member name.
    /// </summary>
    private static string UnqualifiedName(ISymbol symbol)
    {
        var name = symbol.Name;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static bool ExplicitlyImplementsName(ISymbol member, string name)
    {
        IEnumerable<ISymbol> implemented = member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations,
            _ => []
        };

        return implemented.Any(i => string.Equals(i.Name, name, StringComparison.Ordinal));
    }

    internal static bool SignaturesMatch(ISymbol left, ISymbol right)
    {
        if (left is IMethodSymbol leftMethod && right is IMethodSymbol rightMethod)
            return MethodSignaturesMatch(leftMethod, rightMethod);

        if (left is IPropertySymbol leftProp && right is IPropertySymbol rightProp)
            return PropertySignaturesMatch(leftProp, rightProp);

        if (left is IEventSymbol leftEvent && right is IEventSymbol rightEvent)
            return string.Equals(UnqualifiedName(leftEvent), UnqualifiedName(rightEvent), StringComparison.Ordinal);

        return false;
    }

    private static bool MethodSignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(UnqualifiedName(left), UnqualifiedName(right), StringComparison.Ordinal))
            return false;
        if (left.Arity != right.Arity)
            return false;
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
            if (!ParameterTypesMatch(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Method type parameters are distinct symbols on the interface vs
    /// implementation (<c>IFoo.M&lt;T&gt;(T)</c> vs <c>C.M&lt;T&gt;(T)</c>),
    /// so <see cref="SymbolEqualityComparer.Default"/> misses an exact match.
    /// Compare those by ordinal; recurse through constructed named types,
    /// arrays, pointers, tuples, and nullable wrappers so
    /// <c>List&lt;T&gt;</c> / <c>T[]</c> still match. Concrete / named types
    /// that <see cref="SymbolEqualityComparer.Default"/> already equates stay
    /// as today.
    /// </summary>
    private static bool ParameterTypesMatch(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left is ITypeParameterSymbol leftTp
            && leftTp.TypeParameterKind == TypeParameterKind.Method
            && right is ITypeParameterSymbol rightTp
            && rightTp.TypeParameterKind == TypeParameterKind.Method)
        {
            return leftTp.Ordinal == rightTp.Ordinal;
        }

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
            return NamedTypesMatch(leftNamed, rightNamed);

        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank
                && ParameterTypesMatch(leftArray.ElementType, rightArray.ElementType);
        }

        if (left is IPointerTypeSymbol leftPtr && right is IPointerTypeSymbol rightPtr)
            return ParameterTypesMatch(leftPtr.PointedAtType, rightPtr.PointedAtType);

        return false;
    }

    private static bool NamedTypesMatch(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        if (!SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition))
            return false;

        if (left.IsTupleType || right.IsTupleType)
        {
            if (left.TupleElements.Length != right.TupleElements.Length)
                return false;

            for (var i = 0; i < left.TupleElements.Length; i++)
            {
                if (!ParameterTypesMatch(left.TupleElements[i].Type, right.TupleElements[i].Type))
                    return false;
            }

            return true;
        }

        if (left.TypeArguments.Length != right.TypeArguments.Length)
            return false;

        for (var i = 0; i < left.TypeArguments.Length; i++)
        {
            if (!ParameterTypesMatch(left.TypeArguments[i], right.TypeArguments[i]))
                return false;
        }

        return true;
    }

    private static bool PropertySignaturesMatch(IPropertySymbol left, IPropertySymbol right)
    {
        if (left.IsIndexer != right.IsIndexer)
            return false;
        if (!left.IsIndexer)
            return string.Equals(UnqualifiedName(left), UnqualifiedName(right), StringComparison.Ordinal);
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
            if (!ParameterTypesMatch(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
        }

        return true;
    }

    private static List<MemberDeclarationSyntax> GenerateImplementations(
        List<ISymbol> members,
        bool explicitImplementation,
        bool throwNotImplemented)
    {
        var implementations = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? impl = member switch
            {
                IMethodSymbol { MethodKind: MethodKind.Ordinary } method => SyntaxGenerationHelper.CreateMethodStub(
                    method,
                    explicitImplementation,
                    callBase: false,
                    throwNotImplemented),
                IPropertySymbol { IsIndexer: true } indexer => SyntaxGenerationHelper.CreateIndexerStub(
                    indexer,
                    explicitImplementation,
                    throwNotImplemented),
                IPropertySymbol property => SyntaxGenerationHelper.CreatePropertyStub(
                    property,
                    explicitImplementation,
                    throwNotImplemented),
                IEventSymbol evt => SyntaxGenerationHelper.CreateEventStub(
                    evt,
                    explicitImplementation),
                _ => null
            };

            if (impl != null)
            {
                implementations.Add(impl);
            }
        }

        return implementations;
    }

    private static TypeDeclarationSyntax AddMembers(
        TypeDeclarationSyntax typeDeclaration,
        List<MemberDeclarationSyntax> newMembers)
    {
        var members = typeDeclaration.Members.ToList();

        foreach (var member in newMembers)
        {
            members.Add(member
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
        }

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    internal static bool IsImplementableInterfaceMember(ISymbol member) => member switch
    {
        IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
        IPropertySymbol or IEventSymbol => true,
        _ => false
    };

    internal static bool MatchesRequestedMember(ISymbol member, HashSet<string> requested)
    {
        if (requested.Contains(member.Name))
            return true;

        if (member is not IPropertySymbol { IsIndexer: true } indexer)
            return false;

        var withNames = $"this[{string.Join(", ", indexer.Parameters.Select(FormatIndexerParameterDisplay))}]";
        var typesOnly = $"this[{string.Join(",", indexer.Parameters.Select(p => p.Type.ToDisplayString()))}]";
        var typesOnlySpaced = $"this[{string.Join(", ", indexer.Parameters.Select(p => p.Type.ToDisplayString()))}]";
        return requested.Contains(indexer.MetadataName)
            || requested.Contains(withNames)
            || requested.Contains(typesOnly)
            || requested.Contains(typesOnlySpaced);
    }

    private static string FormatIndexerParameterDisplay(IParameterSymbol parameter)
    {
        var type = parameter.Type.ToDisplayString();
        return parameter.RefKind switch
        {
            RefKind.Ref => $"ref {type} {parameter.Name}",
            RefKind.Out => $"out {type} {parameter.Name}",
            RefKind.In => $"in {type} {parameter.Name}",
            RefKind.RefReadOnlyParameter => $"ref readonly {type} {parameter.Name}",
            _ => $"{type} {parameter.Name}"
        };
    }

    /// <summary>
    /// Removes matched implementation declarations from every partial that
    /// holds them. Match by span/kind, not SyntaxNode reference — same seam
    /// as generate_property / generate_method_stub replaceExisting.
    /// Uses <see cref="SyntaxRemoveOptions.KeepExteriorTrivia"/> and
    /// <see cref="SyntaxRemoveOptions.KeepDirectives"/> so a leading
    /// <c>#if</c> / <c>#region</c> on the removed member does not orphan
    /// a following <c>#endif</c> / <c>#endregion</c>.
    /// </summary>
    private static async Task<Solution> RemoveExistingImplementationsAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IEnumerable<ISymbol> existingImplementations,
        CancellationToken cancellationToken)
    {
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();
        var eventDeclaratorsByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int FieldStart, int DeclaratorStart)>>>();

        foreach (var existing in existingImplementations)
        {
            foreach (var reference in existing.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (TryGetEventFieldDeclarator(syntax, out var eventField, out var declarator)
                    && eventField.Parent is TypeDeclarationSyntax eventPart)
                {
                    if (eventField.Declaration.Variables.Count > 1)
                    {
                        AddKeyed(eventDeclaratorsByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                            (eventField.SpanStart, declarator.SpanStart));
                        continue;
                    }

                    AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                        (eventField.SpanStart, eventField.Span.End, eventField.Kind()));
                    continue;
                }

                var memberSyntax = AsRemovableMember(syntax);
                if (memberSyntax == null)
                    continue;
                if (memberSyntax.Parent is not TypeDeclarationSyntax part)
                    continue;

                AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, part.SpanStart,
                    (memberSyntax.SpanStart, memberSyntax.Span.End, memberSyntax.Kind()));
            }
        }

        var trees = membersByTreeAndPart.Keys
            .Concat(eventDeclaratorsByTreeAndPart.Keys)
            .Distinct()
            .ToList();

        foreach (var tree in trees)
        {
            var document = GetDocumentForTree(solution, tree, typeSymbol.Name);
            var treeRoot = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            membersByTreeAndPart.TryGetValue(tree, out var membersByPart);
            eventDeclaratorsByTreeAndPart.TryGetValue(tree, out var eventDeclaratorsByPart);

            var toRemove = new List<MemberDeclarationSyntax>();
            var eventFieldRewrites = new Dictionary<EventFieldDeclarationSyntax, EventFieldDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (!SameSyntaxTree(reference.SyntaxTree, tree))
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax originalPart)
                    continue;
                // The solution root may already carry a target-type
                // annotation (new tree). Rematch by span — annotation does
                // not change SpanStart — so RemoveNodes sees nodes from
                // this root and keeps the annotation on the selected type.
                var part = RematchTypeDeclaration(treeRoot, originalPart);
                if (part == null)
                    continue;

                HashSet<(int Start, int End, SyntaxKind Kind)>? memberKeys = null;
                HashSet<(int FieldStart, int DeclaratorStart)>? eventDeclaratorKeys = null;
                if (membersByPart != null)
                    membersByPart.TryGetValue(part.SpanStart, out memberKeys);
                if (eventDeclaratorsByPart != null)
                    eventDeclaratorsByPart.TryGetValue(part.SpanStart, out eventDeclaratorKeys);

                foreach (var member in part.Members)
                {
                    if (member is EventFieldDeclarationSyntax eventField
                        && eventDeclaratorKeys != null
                        && eventDeclaratorKeys.Count > 0)
                    {
                        var remaining = eventField.Declaration.Variables
                            .Where(v => !eventDeclaratorKeys.Contains((eventField.SpanStart, v.SpanStart)))
                            .ToList();
                        if (remaining.Count == eventField.Declaration.Variables.Count)
                        {
                            if (memberKeys != null
                                && memberKeys.Contains((eventField.SpanStart, eventField.Span.End, eventField.Kind())))
                            {
                                toRemove.Add(eventField);
                            }

                            continue;
                        }

                        if (remaining.Count == 0)
                        {
                            toRemove.Add(eventField);
                            continue;
                        }

                        eventFieldRewrites[eventField] = eventField.WithDeclaration(
                            eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(remaining)));
                        continue;
                    }

                    if (memberKeys != null
                        && memberKeys.Contains((member.SpanStart, member.Span.End, member.Kind())))
                    {
                        toRemove.Add(member);
                    }
                }
            }

            SyntaxNode newRoot = treeRoot;
            if (eventFieldRewrites.Count > 0)
                newRoot = newRoot.ReplaceNodes(eventFieldRewrites.Keys, (original, _) => eventFieldRewrites[original]);

            if (toRemove.Count > 0)
            {
                // KeepDirectives / KeepExteriorTrivia so a leading #if / #region on
                // the removed member does not orphan a following #endif / #endregion.
                newRoot = newRoot.RemoveNodes(
                        toRemove,
                        SyntaxRemoveOptions.KeepExteriorTrivia | SyntaxRemoveOptions.KeepDirectives)
                    ?? newRoot;
            }

            if (eventFieldRewrites.Count == 0 && toRemove.Count == 0)
                continue;

            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    private static Document GetDocumentForTree(Solution solution, SyntaxTree tree, string typeName)
    {
        var document = solution.GetDocument(tree);
        if (document != null)
            return document;

        if (!string.IsNullOrEmpty(tree.FilePath))
        {
            foreach (var id in solution.GetDocumentIdsWithFilePath(tree.FilePath))
            {
                document = solution.GetDocument(id);
                if (document != null)
                    return document;
            }
        }

        throw new RefactoringException(
            ErrorCodes.DocumentNotEditable,
            $"Could not locate a declaring document for type '{typeName}'.");
    }

    private static bool SameSyntaxTree(SyntaxTree left, SyntaxTree right) =>
        left == right
        || (!string.IsNullOrEmpty(left.FilePath)
            && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase));

    private static TypeDeclarationSyntax? RematchTypeDeclaration(SyntaxNode root, TypeDeclarationSyntax original) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.SpanStart == original.SpanStart && t.Identifier.Text == original.Identifier.Text);

    private static void AddKeyed<T>(
        Dictionary<SyntaxTree, Dictionary<int, HashSet<T>>> map,
        SyntaxTree tree,
        int partSpanStart,
        T key)
    {
        if (!map.TryGetValue(tree, out var byPart))
        {
            byPart = new Dictionary<int, HashSet<T>>();
            map[tree] = byPart;
        }

        if (!byPart.TryGetValue(partSpanStart, out var keys))
        {
            keys = new HashSet<T>();
            byPart[partSpanStart] = keys;
        }

        keys.Add(key);
    }

    private static bool TryGetEventFieldDeclarator(
        SyntaxNode syntax,
        out EventFieldDeclarationSyntax eventField,
        out VariableDeclaratorSyntax declarator)
    {
        if (syntax is VariableDeclaratorSyntax variable
            && variable.Parent?.Parent is EventFieldDeclarationSyntax field)
        {
            eventField = field;
            declarator = variable;
            return true;
        }

        eventField = null!;
        declarator = null!;
        return false;
    }

    private static MemberDeclarationSyntax? AsRemovableMember(SyntaxNode syntax)
    {
        if (syntax is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
            or EventDeclarationSyntax or EventFieldDeclarationSyntax)
        {
            return (MemberDeclarationSyntax)syntax;
        }

        var ancestor = syntax.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        return ancestor is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
            or EventDeclarationSyntax or EventFieldDeclarationSyntax
            ? ancestor
            : null;
    }

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted <paramref name="line"/>
    /// keeps today's <c>FirstOrDefault</c> pick, including when several
    /// same-named types exist (nested vs outer, or two namespaces). When set,
    /// picks the type whose identifier or declaration span covers that 1-based
    /// line (same exclusive-end coverage as
    /// <c>GenerateOverridesOperation.SpanCoversLine</c>). Prefer the identifier
    /// hit, then the smallest containing type. Nested types participate
    /// (<c>DescendantNodes</c>). Do not require the declaration to start on
    /// <paramref name="line"/> — a split declaration may put the identifier
    /// on a continuation line. If nothing covers this line, keep today's
    /// first-match rather than inventing a not-found. After
    /// <see cref="RemoveExistingImplementationsAcrossPartialsAsync"/>, recover
    /// the selected type from <see cref="TargetTypeAnnotation"/> — do not
    /// reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static TypeDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line)
    {
        var candidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        if (candidates.Count == 0)
            return null;

        if (!line.HasValue)
            return candidates.FirstOrDefault();

        // Do not require the declaration to start on `line` — a split
        // type's identifier may live on a continuation line whose
        // declaration span still covers that line. Prefer the identifier
        // hit, then the smallest containing type (nested over outer).
        // Do not silently pick the first when a covering node exists
        // elsewhere — scan every candidate. If nothing covers this line,
        // keep today's first-match rather than inventing a not-found.
        return candidates
            .Where(t => TypeCoversLine(t, line.Value))
            .OrderBy(t => IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? candidates.FirstOrDefault();
    }

    private static bool TypeCoversLine(TypeDeclarationSyntax type, int line) =>
        IdentifierCoversLine(type, line) ||
        SpanCoversLine(type.GetLocation().GetLineSpan(), line);

    private static bool IdentifierCoversLine(TypeDeclarationSyntax type, int line) =>
        SpanCoversLine(type.Identifier.GetLocation().GetLineSpan(), line);

    /// <summary>
    /// 1-based line coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so a span that ends at the start of a line does not
    /// cover that line. Treating the end as inclusive would let the first
    /// line of an adjacent type also match the previous declaration. Same
    /// exclusive-end idea as <c>GenerateOverridesOperation.SpanCoversLine</c>.
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
    /// Creates a preview result describing generate vs replace.
    /// When an existing member lives in another partial, also includes a
    /// Modify pending change per distinct declaring file with that member
    /// as <c>BeforeSnippet</c> — same as generate_property / constructor
    /// replaceExisting preview.
    /// </summary>
    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        ImplementInterfaceParams @params,
        List<ISymbol> membersToGenerate,
        List<ISymbol> membersToReplace,
        List<MemberDeclarationSyntax> implementations,
        Dictionary<ISymbol, ISymbol> replacements,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var selectedMembers = membersToGenerate.Concat(membersToReplace).ToList();
        var description = membersToReplace.Count > 0 || @params.ReplaceExisting
            ? BuildPreviewDescription(@params.InterfaceName, membersToGenerate, membersToReplace)
            : $"Implement {@params.InterfaceName} members: {string.Join(", ", selectedMembers.Select(m => m.Name))}";
        var implCode = string.Join("\n\n",
            implementations.Select(i => i.NormalizeWhitespace().ToFullString()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = description,
                BeforeSnippet = membersToReplace.Count > 0
                    ? $"// Type '{@params.TypeName}' (replacing existing interface members)"
                    : $"// End of type '{@params.TypeName}'",
                AfterSnippet = implCode
            }
        };

        if (replacements.Count > 0)
        {
            var sourcePath = PathResolver.NormalizePath(@params.SourceFile);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in replacements.Values)
            {
                foreach (var reference in existing.DeclaringSyntaxReferences)
                {
                    var syntax = await reference.GetSyntaxAsync(cancellationToken);
                    var memberSyntax = AsRemovableMember(syntax);
                    if (memberSyntax == null)
                        continue;

                    var declaringDocument = solution.GetDocument(syntax.SyntaxTree);
                    var filePath = declaringDocument?.FilePath ?? syntax.SyntaxTree.FilePath;
                    if (string.IsNullOrWhiteSpace(filePath))
                        continue;

                    var normalized = PathResolver.NormalizePath(filePath);
                    if (!seenFiles.Add(normalized))
                        continue;
                    if (string.Equals(normalized, sourcePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    pendingChanges.Add(new PendingChange
                    {
                        File = filePath,
                        ChangeType = ChangeKind.Modify,
                        Description = $"Remove existing {DescribeMemberKind(existing)} '{existing.Name}' from {@params.TypeName}",
                        BeforeSnippet = memberSyntax.NormalizeWhitespace().ToFullString(),
                        AfterSnippet = $"// {DescribeMemberKind(existing)} removed"
                    });
                }
            }
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    internal static string BuildPreviewDescription(
        string interfaceName,
        IReadOnlyList<ISymbol> membersToGenerate,
        IReadOnlyList<ISymbol> membersToReplace)
    {
        var generated = string.Join(", ", membersToGenerate.Select(m => m.Name));
        var replaced = string.Join(", ", membersToReplace.Select(m => m.Name));

        if (membersToReplace.Count == 0)
            return $"Generate {interfaceName} members: {generated}";
        if (membersToGenerate.Count == 0)
            return $"Replace existing {interfaceName} members: {replaced}";
        return $"Generate {interfaceName} members: {generated}; replace existing {interfaceName} members: {replaced}";
    }

    private static string DescribeMemberKind(ISymbol member) => member switch
    {
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => "member"
    };
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
/// Honors <c>replaceExisting</c> to include already-implemented interface
/// members, remove those declarations (including across partials) by
/// signature, and insert a standard generated stub. Property/event accessors
/// are never emitted as ordinary methods. Extra modifiers on the old
/// implementation are not copied.
/// </summary>
public sealed class ImplementInterfaceOperation : RefactoringOperationBase<ImplementInterfaceParams>
{
    /// <summary>
    /// Creates a new implement interface operation.
    /// </summary>
    public ImplementInterfaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ImplementInterfaceParams @params)
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

        // Find the type declaration
        var typeDeclaration = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

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
            solution = await RemoveExistingImplementationsAcrossPartialsAsync(
                solution, typeSymbol, replacements.Values, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{@params.TypeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            typeDeclaration = FindTypeDeclaration(root, @params.TypeName, typeDeclaration.SpanStart)
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
        var isExplicit = member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0
                || method.MethodKind == MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };

        return explicitImplementation ? isExplicit : !isExplicit;
    }

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
    /// Compare those by ordinal; keep concrete / named types as today.
    /// </summary>
    private static bool ParameterTypesMatch(ITypeSymbol left, ITypeSymbol right)
    {
        if (left is ITypeParameterSymbol leftTp
            && leftTp.TypeParameterKind == TypeParameterKind.Method
            && right is ITypeParameterSymbol rightTp
            && rightTp.TypeParameterKind == TypeParameterKind.Method)
        {
            return leftTp.Ordinal == rightTp.Ordinal;
        }

        return SymbolEqualityComparer.Default.Equals(left, right);
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
            if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
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

        foreach (var existing in existingImplementations)
        {
            foreach (var reference in existing.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                var memberSyntax = AsRemovableMember(syntax);
                if (memberSyntax == null)
                    continue;
                if (memberSyntax.Parent is not TypeDeclarationSyntax part)
                    continue;

                if (!membersByTreeAndPart.TryGetValue(syntax.SyntaxTree, out var byPart))
                {
                    byPart = new Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>();
                    membersByTreeAndPart[syntax.SyntaxTree] = byPart;
                }

                if (!byPart.TryGetValue(part.SpanStart, out var keys))
                {
                    keys = new HashSet<(int Start, int End, SyntaxKind Kind)>();
                    byPart[part.SpanStart] = keys;
                }

                keys.Add((memberSyntax.SpanStart, memberSyntax.Span.End, memberSyntax.Kind()));
            }
        }

        foreach (var (tree, byPart) in membersByTreeAndPart)
        {
            var document = solution.GetDocument(tree)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate a declaring document for type '{typeSymbol.Name}'.");
            var treeRoot = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var toRemove = new List<MemberDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree != tree)
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax part)
                    continue;
                if (!byPart.TryGetValue(part.SpanStart, out var keys) || keys.Count == 0)
                    continue;

                foreach (var member in part.Members)
                {
                    if (keys.Contains((member.SpanStart, member.Span.End, member.Kind())))
                        toRemove.Add(member);
                }
            }

            if (toRemove.Count == 0)
                continue;

            // KeepDirectives / KeepExteriorTrivia so a leading #if / #region on
            // the removed member does not orphan a following #endif / #endregion.
            var newRoot = treeRoot.RemoveNodes(
                    toRemove,
                    SyntaxRemoveOptions.KeepExteriorTrivia | SyntaxRemoveOptions.KeepDirectives)
                ?? treeRoot;
            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
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

    private static TypeDeclarationSyntax? FindTypeDeclaration(SyntaxNode root, string typeName, int preferredSpanStart)
    {
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();
        return matches.FirstOrDefault(t => t.SpanStart == preferredSpanStart) ?? matches.FirstOrDefault();
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

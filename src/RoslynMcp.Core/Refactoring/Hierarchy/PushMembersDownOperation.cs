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
/// Moves selected members from a base type down onto derived types.
/// </summary>
public sealed class PushMembersDownOperation : RefactoringOperationBase<PushMembersDownParams>
{
    /// <summary>
    /// Creates a new push-members-down operation.
    /// </summary>
    public PushMembersDownOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(PushMembersDownParams @params) => Validate(@params);

    /// <summary>
    /// Validates push-members-down parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(PushMembersDownParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (@params.Members == null || @params.Members.Count == 0 || @params.Members.All(string.IsNullOrWhiteSpace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "members is required.");

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
        PushMembersDownParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var sourceDecl = FindTypeDeclaration(root, @params.TypeName);
        if (sourceDecl == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        var sourceSymbol = semanticModel.GetDeclaredSymbol(sourceDecl, cancellationToken) as INamedTypeSymbol;
        if (sourceSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        var members = FindMembersToPush(sourceDecl, @params.Members, semanticModel, cancellationToken);
        var targets = await GetDerivedTypes(sourceSymbol, @params.TargetDerivedTypes, cancellationToken);

        if (targets.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.DerivedClassesNotFound,
                $"Type '{sourceSymbol.Name}' has no derived types to push members to.");
        }

        foreach (var target in targets)
            ValidateDerivedIsEditable(target);

        var leaveAbstract = @params.LeaveAbstract && sourceSymbol.TypeKind != TypeKind.Interface;
        ValidateMembersForPush(members, sourceSymbol, targets, leaveAbstract);

        var pushedNames = members.Select(m => m.Name).ToList();
        var derivedUpdates = new List<DerivedUpdate>();

        foreach (var target in targets)
        {
            var original = await GetTypeDeclarationAsync(target, cancellationToken);
            var copies = members
                .Select(member => ConvertForDerived(member.Syntax, target, leaveAbstract))
                .ToList();
            derivedUpdates.Add(new DerivedUpdate(target, original, AddMembersToType(original, copies)));
        }

        var sourceReplacement = BuildSourceReplacement(sourceDecl, members, sourceSymbol, leaveAbstract);

        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                sourceSymbol,
                pushedNames,
                sourceDecl,
                sourceReplacement,
                derivedUpdates);
        }

        var solution = await ApplyChangesAsync(
            document,
            sourceDecl,
            sourceReplacement,
            derivedUpdates,
            cancellationToken);

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
                Name = sourceSymbol.Name,
                FullyQualifiedName = sourceSymbol.ToDisplayString(),
                Kind = sourceSymbol.TypeKind == TypeKind.Interface
                    ? Contracts.Enums.SymbolKind.Interface
                    : Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private static TypeDeclarationSyntax? FindTypeDeclaration(SyntaxNode root, string typeName)
    {
        var simpleName = typeName.Contains('.')
            ? typeName[(typeName.LastIndexOf('.') + 1)..]
            : typeName;

        return root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == simpleName);
    }

    internal static async Task<IReadOnlyList<INamedTypeSymbol>> GetDerivedTypes(
        INamedTypeSymbol source,
        IReadOnlyList<string>? targetNames,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var discovered = new List<INamedTypeSymbol>();

        if (source.TypeKind == TypeKind.Interface)
        {
            var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(
                source, solution, transitive: false, cancellationToken: cancellationToken);
            discovered.AddRange(derivedInterfaces);

            var implementations = await SymbolFinder.FindImplementationsAsync(
                source, solution, cancellationToken: cancellationToken);
            foreach (var implementation in implementations.OfType<INamedTypeSymbol>())
            {
                if (IsDirectInterface(implementation, source))
                    discovered.Add(implementation);
            }
        }
        else
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(
                source, solution, transitive: false, cancellationToken: cancellationToken);
            discovered.AddRange(derived);
        }

        var unique = DistinctTypes(discovered);

        if (targetNames == null || targetNames.Count == 0 || targetNames.All(string.IsNullOrWhiteSpace))
            return unique;

        var selected = new List<INamedTypeSymbol>();
        foreach (var name in targetNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var match = unique.FirstOrDefault(candidate =>
                candidate.Name.Equals(name, StringComparison.Ordinal) ||
                candidate.ToDisplayString().Equals(name, StringComparison.Ordinal));

            if (match == null)
            {
                throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"Type '{name}' is not a derived type of '{source.Name}'.");
            }

            selected.Add(match);
        }

        return DistinctTypes(selected);
    }

    private async Task<IReadOnlyList<INamedTypeSymbol>> GetDerivedTypes(
        INamedTypeSymbol source,
        IReadOnlyList<string>? targetNames,
        CancellationToken cancellationToken)
    {
        return await GetDerivedTypes(source, targetNames, Context.Solution, cancellationToken);
    }

    private static bool IsDirectInterface(INamedTypeSymbol type, INamedTypeSymbol iface)
    {
        return type.Interfaces.Any(implemented =>
            SymbolEqualityComparer.Default.Equals(implemented, iface) ||
            SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface.OriginalDefinition));
    }

    private static List<INamedTypeSymbol> DistinctTypes(IEnumerable<INamedTypeSymbol> types)
    {
        var unique = new List<INamedTypeSymbol>();
        foreach (var type in types)
        {
            if (unique.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
                continue;
            unique.Add(type);
        }

        return unique;
    }

    /// <summary>
    /// Rejects derived types that live only in metadata.
    /// </summary>
    internal static void ValidateDerivedIsEditable(INamedTypeSymbol derived)
    {
        if (!derived.Locations.Any(location => location.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.DerivedClassNotEditable,
                $"Derived type '{derived.Name}' is not editable (defined in an external assembly).");
        }
    }

    private static List<PushableMember> FindMembersToPush(
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<string> memberNames,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var requested = new HashSet<string>(memberNames.Where(n => !string.IsNullOrWhiteSpace(n)));
        var found = new List<PushableMember>();

        foreach (var (name, symbol, syntax) in EnumerateDeclaredMembers(typeDeclaration, semanticModel, cancellationToken))
        {
            if (!requested.Contains(name))
                continue;

            if (symbol == null)
            {
                throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    $"Could not resolve symbol for member '{name}'.");
            }

            if (!IsSupportedMember(symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"Member '{name}' cannot be pushed down.");
            }

            found.Add(new PushableMember(name, symbol, syntax));
            requested.Remove(name);
        }

        if (requested.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found: {string.Join(", ", requested)}");
        }

        return found;
    }

    private static IEnumerable<(string Name, ISymbol? Symbol, MemberDeclarationSyntax Syntax)> EnumerateDeclaredMembers(
        TypeDeclarationSyntax typeDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var member in typeDeclaration.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    yield return (method.Identifier.Text, semanticModel.GetDeclaredSymbol(method, cancellationToken), method);
                    break;
                case PropertyDeclarationSyntax property:
                    yield return (property.Identifier.Text, semanticModel.GetDeclaredSymbol(property, cancellationToken), property);
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), field);
                    }
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), eventField);
                    }
                    break;
                case EventDeclarationSyntax eventDecl:
                    yield return (eventDecl.Identifier.Text, semanticModel.GetDeclaredSymbol(eventDecl, cancellationToken), eventDecl);
                    break;
            }
        }
    }

    private static bool IsSupportedMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
        IPropertySymbol => true,
        IFieldSymbol => true,
        IEventSymbol => true,
        _ => false
    };

    private static void ValidateMembersForPush(
        IReadOnlyList<PushableMember> members,
        INamedTypeSymbol source,
        IReadOnlyList<INamedTypeSymbol> targets,
        bool leaveAbstract)
    {
        foreach (var member in members)
        {
            if (leaveAbstract && !CanBeAbstract(member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"Member '{member.Name}' cannot be left as abstract on '{source.Name}'.");
            }

            if (source.TypeKind != TypeKind.Interface &&
                !leaveAbstract &&
                ImplementsInterfaceMember(member.Symbol, source))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotInterfaceCompatible,
                    $"Member '{member.Name}' implements an interface contract on '{source.Name}' and cannot be removed.");
            }

            foreach (var target in targets)
            {
                if (!CanMoveMember(member.Symbol, target))
                {
                    if (target.TypeKind == TypeKind.Interface && !IsInterfaceCompatible(member.Symbol))
                    {
                        throw new RefactoringException(
                            ErrorCodes.MemberNotInterfaceCompatible,
                            $"Member '{member.Name}' cannot be pushed to interface '{target.Name}'.");
                    }

                    throw new RefactoringException(
                        ErrorCodes.ConflictsWithExistingMember,
                        $"Member '{member.Name}' already exists in '{target.Name}'.");
                }
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="member"/> can be copied onto <paramref name="target"/>.
    /// </summary>
    internal static bool CanMoveMember(ISymbol member, INamedTypeSymbol target)
    {
        if (HasConflict(target, member))
            return false;

        if (target.TypeKind == TypeKind.Interface && !IsInterfaceCompatible(member))
            return false;

        return true;
    }

    private static bool CanBeAbstract(ISymbol member) =>
        member is IMethodSymbol or IPropertySymbol;

    private static bool ImplementsInterfaceMember(ISymbol member, INamedTypeSymbol source)
    {
        foreach (var iface in source.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(member.Name))
            {
                var implementation = source.FindImplementationForInterfaceMember(ifaceMember);
                if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, member))
                    return true;
            }
        }

        return false;
    }

    private static bool HasConflict(INamedTypeSymbol target, ISymbol member)
    {
        foreach (var existing in target.GetMembers(member.Name))
        {
            if (existing.IsImplicitlyDeclared)
                continue;

            if (member is IMethodSymbol method && existing is IMethodSymbol existingMethod)
            {
                if (SignaturesMatch(method, existingMethod))
                    return true;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool SignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        if (left.TypeParameters.Length != right.TypeParameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
        }

        return true;
    }

    private static bool IsInterfaceCompatible(ISymbol member)
    {
        if (member.IsStatic)
            return false;

        if (member.DeclaredAccessibility != Accessibility.Public)
            return false;

        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
    }

    private static MemberDeclarationSyntax ConvertForDerived(
        MemberDeclarationSyntax member,
        INamedTypeSymbol target,
        bool leaveAbstract)
    {
        if (target.TypeKind == TypeKind.Interface)
            return ConvertToInterfaceMember(member);

        return leaveAbstract
            ? AddOverrideModifier(member)
            : StripHierarchyModifiers(member);
    }

    private static MemberDeclarationSyntax ConvertToInterfaceMember(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(SyntaxFactory.TokenList())
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => ToInterfaceProperty(property),
            EventDeclarationSyntax eventDecl => eventDecl
                .WithModifiers(SyntaxFactory.TokenList())
                .WithAccessorList(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField => SyntaxFactory.EventFieldDeclaration(eventField.Declaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            _ => throw new RefactoringException(
                ErrorCodes.MemberNotInterfaceCompatible,
                "Member cannot be declared on an interface.")
        };
    }

    private static PropertyDeclarationSyntax ToInterfaceProperty(PropertyDeclarationSyntax property)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (property.AccessorList != null)
        {
            foreach (var accessor in property.AccessorList.Accessors)
            {
                accessors.Add(accessor
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return property
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static MemberDeclarationSyntax ConvertToAbstract(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(ToAbstractModifiers(method.Modifiers))
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => ToAbstractProperty(property),
            _ => throw new RefactoringException(
                ErrorCodes.MemberNotMoveable,
                "Only methods and properties can be left as abstract members.")
        };
    }

    private static PropertyDeclarationSyntax ToAbstractProperty(PropertyDeclarationSyntax property)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (property.AccessorList != null)
        {
            foreach (var accessor in property.AccessorList.Accessors)
            {
                accessors.Add(accessor
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return property
            .WithModifiers(ToAbstractModifiers(property.Modifiers))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static MemberDeclarationSyntax AddOverrideModifier(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => EnsureMethodBody(method.WithModifiers(ToOverrideModifiers(method.Modifiers)))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => property.WithModifiers(ToOverrideModifiers(property.Modifiers))
                .NormalizeWhitespace(),
            _ => member.NormalizeWhitespace()
        };
    }

    private static MethodDeclarationSyntax EnsureMethodBody(MethodDeclarationSyntax method)
    {
        if (method.Body != null || method.ExpressionBody != null)
            return method;

        return method
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.NotImplementedException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList()))));
    }

    private static MemberDeclarationSyntax StripHierarchyModifiers(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => EnsureMethodBody(method
                    .WithModifiers(StripModifiers(
                        method.Modifiers,
                        SyntaxKind.VirtualKeyword,
                        SyntaxKind.OverrideKeyword,
                        SyntaxKind.AbstractKeyword,
                        SyntaxKind.NewKeyword,
                        SyntaxKind.SealedKeyword)))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => property
                .WithModifiers(StripModifiers(
                    property.Modifiers,
                    SyntaxKind.VirtualKeyword,
                    SyntaxKind.OverrideKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.NewKeyword,
                    SyntaxKind.SealedKeyword))
                .NormalizeWhitespace(),
            FieldDeclarationSyntax field => field.NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField => eventField.NormalizeWhitespace(),
            EventDeclarationSyntax eventDecl => eventDecl
                .WithModifiers(StripModifiers(
                    eventDecl.Modifiers,
                    SyntaxKind.VirtualKeyword,
                    SyntaxKind.OverrideKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.NewKeyword,
                    SyntaxKind.SealedKeyword))
                .NormalizeWhitespace(),
            _ => member.NormalizeWhitespace()
        };
    }

    private static SyntaxTokenList ToAbstractModifiers(SyntaxTokenList modifiers)
    {
        var tokens = StripModifierKinds(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.SealedKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.NewKeyword)
            .ToList();

        if (!HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        return SyntaxFactory.TokenList(tokens);
    }

    private static SyntaxTokenList ToOverrideModifiers(SyntaxTokenList modifiers)
    {
        var tokens = StripModifierKinds(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.NewKeyword,
                SyntaxKind.SealedKeyword)
            .ToList();

        if (modifiers.Any(SyntaxKind.PrivateKeyword) || !HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
        return SyntaxFactory.TokenList(tokens);
    }

    private static SyntaxTokenList StripModifiers(SyntaxTokenList modifiers, params SyntaxKind[] kinds) =>
        SyntaxFactory.TokenList(StripModifierKinds(modifiers, kinds));

    private static IEnumerable<SyntaxToken> StripModifierKinds(SyntaxTokenList modifiers, params SyntaxKind[] kinds)
    {
        var kindSet = kinds.ToHashSet();
        return modifiers.Where(token => !kindSet.Contains(token.Kind()));
    }

    private static bool HasAccessibility(IEnumerable<SyntaxToken> modifiers) =>
        modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword) ||
            token.IsKind(SyntaxKind.ProtectedKeyword) ||
            token.IsKind(SyntaxKind.InternalKeyword) ||
            token.IsKind(SyntaxKind.PrivateKeyword));

    private static TypeDeclarationSyntax BuildSourceReplacement(
        TypeDeclarationSyntax sourceDecl,
        IReadOnlyList<PushableMember> members,
        INamedTypeSymbol source,
        bool leaveAbstract)
    {
        if (source.TypeKind == TypeKind.Interface)
            return sourceDecl;

        var pushedSyntax = members.Select(m => m.Syntax).ToHashSet();
        var newMembers = new List<MemberDeclarationSyntax>();

        foreach (var member in sourceDecl.Members)
        {
            if (!pushedSyntax.Contains(member))
            {
                newMembers.Add(member);
                continue;
            }

            if (leaveAbstract)
                newMembers.Add(ConvertToAbstract(member));
        }

        var updated = sourceDecl.WithMembers(SyntaxFactory.List(newMembers));

        if (leaveAbstract &&
            sourceDecl is ClassDeclarationSyntax &&
            !sourceDecl.Modifiers.Any(SyntaxKind.AbstractKeyword))
        {
            updated = updated.AddModifiers(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        }

        return updated;
    }

    /// <summary>
    /// Inserts a member into a type declaration.
    /// </summary>
    internal static TypeDeclarationSyntax AddMemberToType(TypeDeclarationSyntax typeDecl, MemberDeclarationSyntax member)
    {
        return AddMembersToType(typeDecl, [member]);
    }

    private static TypeDeclarationSyntax AddMembersToType(
        TypeDeclarationSyntax typeDecl,
        IReadOnlyList<MemberDeclarationSyntax> members)
    {
        var formatted = members.Select(member => member
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));

        return typeDecl.WithMembers(typeDecl.Members.AddRange(formatted));
    }

    private static async Task<TypeDeclarationSyntax> GetTypeDeclarationAsync(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        var syntaxRef = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            throw new RefactoringException(
                ErrorCodes.DerivedClassNotEditable,
                $"Derived type '{type.Name}' is not editable (defined in an external assembly).");
        }

        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken) as TypeDeclarationSyntax;
        if (syntax == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate declaration for '{type.Name}'.");
        }

        return syntax;
    }

    private async Task<Solution> ApplyChangesAsync(
        Document sourceDocument,
        TypeDeclarationSyntax sourceDecl,
        TypeDeclarationSyntax newSource,
        IReadOnlyList<DerivedUpdate> derivedUpdates,
        CancellationToken cancellationToken)
    {
        var replacements = new List<(SyntaxTree Tree, SyntaxNode Original, SyntaxNode Replacement)>
        {
            (sourceDecl.SyntaxTree, sourceDecl, newSource)
        };

        foreach (var update in derivedUpdates)
            replacements.Add((update.Original.SyntaxTree, update.Original, update.Updated));

        var solution = sourceDocument.Project.Solution;

        foreach (var group in replacements.GroupBy(replacement => replacement.Tree))
        {
            var document = solution.GetDocument(group.Key);
            if (document == null)
            {
                throw new RefactoringException(
                    ErrorCodes.DerivedClassNotEditable,
                    "A target type is not part of the workspace.");
            }

            var currentRoot = await document.GetSyntaxRootAsync(cancellationToken);
            if (currentRoot == null)
                throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var map = group.ToDictionary(item => item.Original, item => item.Replacement);
            var newRoot = currentRoot.ReplaceNodes(map.Keys, (original, _) => map[original]);
            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        return solution;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        PushMembersDownParams @params,
        INamedTypeSymbol source,
        IReadOnlyList<string> pushedNames,
        TypeDeclarationSyntax originalSource,
        TypeDeclarationSyntax updatedSource,
        IReadOnlyList<DerivedUpdate> derivedUpdates)
    {
        var memberList = string.Join(", ", pushedNames);
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = source.TypeKind == TypeKind.Interface
                    ? $"Keep {memberList} on {@params.TypeName}"
                    : @params.LeaveAbstract
                        ? $"Leave {memberList} as abstract on {@params.TypeName}"
                        : $"Remove {memberList} from {@params.TypeName}",
                BeforeSnippet = originalSource.Identifier.Text,
                AfterSnippet = updatedSource.NormalizeWhitespace().ToFullString()
            }
        };

        foreach (var update in derivedUpdates)
        {
            var location = update.Type.Locations.FirstOrDefault(l => l.IsInSource);
            var file = location?.SourceTree?.FilePath ?? @params.SourceFile;
            pendingChanges.Add(new PendingChange
            {
                File = file,
                ChangeType = ChangeKind.Modify,
                Description = $"Add {memberList} to {update.Type.Name}",
                BeforeSnippet = update.Original.Identifier.Text,
                AfterSnippet = update.Updated.NormalizeWhitespace().ToFullString()
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record PushableMember(string Name, ISymbol Symbol, MemberDeclarationSyntax Syntax);

    private sealed record DerivedUpdate(
        INamedTypeSymbol Type,
        TypeDeclarationSyntax Original,
        TypeDeclarationSyntax Updated);
}

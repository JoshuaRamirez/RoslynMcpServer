using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Moves selected members from a derived type onto an existing base class or interface.
/// </summary>
public sealed class PullMembersUpOperation : RefactoringOperationBase<PullMembersUpParams>
{
    /// <summary>
    /// Creates a new pull-members-up operation.
    /// </summary>
    public PullMembersUpOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(PullMembersUpParams @params) => Validate(@params);

    /// <summary>
    /// Validates pull-members-up parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(PullMembersUpParams @params)
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
        PullMembersUpParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var derivedDecl = FindTypeDeclaration(root, @params.TypeName);
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
        ValidateTarget(target);

        var members = FindMembersToPull(derivedDecl, @params.Members, semanticModel, cancellationToken);
        ValidateMembersForPull(members, derivedSymbol, target, semanticModel);

        var pulledNames = members.Select(m => m.Name).ToList();
        var targetMembers = members
            .Select(m => ConvertForTarget(m.Syntax, target, @params.MakeAbstract))
            .ToList();
        var derivedReplacement = BuildDerivedReplacement(
            derivedDecl,
            members,
            target,
            @params.MakeAbstract);

        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                target,
                pulledNames,
                targetMembers,
                derivedDecl,
                derivedReplacement);
        }

        var solution = await ApplyChangesAsync(
            document,
            derivedDecl,
            derivedReplacement,
            target,
            targetMembers,
            @params.MakeAbstract,
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
                Name = target.Name,
                FullyQualifiedName = target.ToDisplayString(),
                Kind = target.TypeKind == TypeKind.Interface
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

    internal static INamedTypeSymbol GetTargetBaseType(INamedTypeSymbol derived, string? targetTypeName)
    {
        var classBases = new List<INamedTypeSymbol>();
        for (var baseType = derived.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType.SpecialType == SpecialType.System_Object)
                break;
            classBases.Add(baseType);
        }

        var interfaces = derived.AllInterfaces.ToList();

        if (string.IsNullOrWhiteSpace(targetTypeName))
        {
            if (classBases.Count > 0)
                return classBases[0];

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
                $"Type '{derived.Name}' has no base class or interface to pull members to.");
        }

        var candidates = classBases.Concat(interfaces);
        var match = candidates.FirstOrDefault(candidate =>
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

    internal static void ValidateTarget(INamedTypeSymbol target)
    {
        if (target.TypeKind == TypeKind.Class && target.IsSealed)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassIsSealed,
                $"Base class '{target.Name}' is sealed.");
        }

        if (!target.Locations.Any(location => location.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotEditable,
                $"Base type '{target.Name}' is not editable (defined in an external assembly).");
        }
    }

    private static List<PullableMember> FindMembersToPull(
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<string> memberNames,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var requested = new HashSet<string>(memberNames.Where(n => !string.IsNullOrWhiteSpace(n)));
        var found = new List<PullableMember>();

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
                    $"Member '{name}' cannot be pulled up.");
            }

            found.Add(new PullableMember(name, symbol, syntax));
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

    private static void ValidateMembersForPull(
        IReadOnlyList<PullableMember> members,
        INamedTypeSymbol derived,
        INamedTypeSymbol target,
        SemanticModel semanticModel)
    {
        var pulledNames = members.Select(m => m.Name).ToHashSet();

        foreach (var member in members)
        {
            if (HasConflict(target, member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.ConflictsWithExistingMember,
                    $"Member '{member.Name}' already exists in '{target.Name}'.");
            }

            if (target.TypeKind == TypeKind.Interface && !IsInterfaceCompatible(member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotInterfaceCompatible,
                    $"Member '{member.Name}' cannot be pulled to interface '{target.Name}'.");
            }

            var dependency = FindDerivedOnlyDependency(member, derived, pulledNames, semanticModel);
            if (dependency != null)
            {
                throw new RefactoringException(
                    ErrorCodes.MemberDependsOnDerived,
                    $"Member '{member.Name}' depends on '{dependency}' which is not accessible from '{target.Name}'.");
            }
        }
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

    private static string? FindDerivedOnlyDependency(
        PullableMember member,
        INamedTypeSymbol derived,
        HashSet<string> pulledNames,
        SemanticModel semanticModel)
    {
        foreach (var identifier in member.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var referenced = semanticModel.GetSymbolInfo(identifier).Symbol;
            if (referenced == null)
                continue;

            if (referenced is IParameterSymbol or ILocalSymbol or ITypeParameterSymbol)
                continue;

            if (referenced.ContainingType == null)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(referenced.ContainingType, derived))
                continue;

            if (referenced.Kind is not (Microsoft.CodeAnalysis.SymbolKind.Field
                or Microsoft.CodeAnalysis.SymbolKind.Method
                or Microsoft.CodeAnalysis.SymbolKind.Property
                or Microsoft.CodeAnalysis.SymbolKind.Event))
                continue;

            if (SymbolEqualityComparer.Default.Equals(referenced, member.Symbol))
                continue;

            if (referenced is IMethodSymbol referencedMethod &&
                member.Symbol is IMethodSymbol memberMethod &&
                SignaturesMatch(referencedMethod, memberMethod) &&
                referenced.Name == member.Name)
            {
                continue;
            }

            if (pulledNames.Contains(referenced.Name))
                continue;

            return referenced.Name;
        }

        return null;
    }

    private static MemberDeclarationSyntax ConvertForTarget(
        MemberDeclarationSyntax member,
        INamedTypeSymbol target,
        bool makeAbstract)
    {
        if (target.TypeKind == TypeKind.Interface)
            return ConvertToInterfaceMember(member);

        return makeAbstract
            ? ConvertToAbstract(member)
            : ConvertToVirtualOnBase(member);
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
                "Only methods and properties can be pulled up as abstract members.")
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

    private static MemberDeclarationSyntax ConvertToVirtualOnBase(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(AdjustBaseClassModifiers(method.Modifiers, addVirtual: !method.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => property
                .WithModifiers(AdjustBaseClassModifiers(property.Modifiers, addVirtual: !property.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .NormalizeWhitespace(),
            FieldDeclarationSyntax field => field
                .WithModifiers(AdjustBaseClassModifiers(field.Modifiers, addVirtual: false))
                .NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField => eventField
                .WithModifiers(AdjustBaseClassModifiers(eventField.Modifiers, addVirtual: false))
                .NormalizeWhitespace(),
            EventDeclarationSyntax eventDecl => eventDecl
                .WithModifiers(AdjustBaseClassModifiers(eventDecl.Modifiers, addVirtual: !eventDecl.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .NormalizeWhitespace(),
            _ => member.NormalizeWhitespace()
        };
    }

    private static SyntaxTokenList ToAbstractModifiers(SyntaxTokenList modifiers)
    {
        var tokens = StripModifiers(
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

    private static SyntaxTokenList AdjustBaseClassModifiers(SyntaxTokenList modifiers, bool addVirtual)
    {
        var tokens = StripModifiers(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.NewKeyword)
            .ToList();

        if (modifiers.Any(SyntaxKind.PrivateKeyword) || !HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        var alreadyOverridable = tokens.Any(t =>
            t.IsKind(SyntaxKind.VirtualKeyword) ||
            t.IsKind(SyntaxKind.OverrideKeyword) ||
            t.IsKind(SyntaxKind.AbstractKeyword));

        if (addVirtual && !alreadyOverridable)
            tokens.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

        return SyntaxFactory.TokenList(tokens);
    }

    private static IEnumerable<SyntaxToken> StripModifiers(SyntaxTokenList modifiers, params SyntaxKind[] kinds)
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

    private static TypeDeclarationSyntax BuildDerivedReplacement(
        TypeDeclarationSyntax derivedDecl,
        IReadOnlyList<PullableMember> members,
        INamedTypeSymbol target,
        bool makeAbstract)
    {
        var pulledSyntax = members.Select(m => m.Syntax).ToHashSet();
        var keepAsOverride = makeAbstract && target.TypeKind != TypeKind.Interface;
        var newMembers = new List<MemberDeclarationSyntax>();

        foreach (var member in derivedDecl.Members)
        {
            if (!pulledSyntax.Contains(member))
            {
                newMembers.Add(member);
                continue;
            }

            if (target.TypeKind == TypeKind.Interface)
            {
                newMembers.Add(member);
                continue;
            }

            if (keepAsOverride)
            {
                newMembers.Add(AddOverrideModifier(member));
            }
        }

        return derivedDecl.WithMembers(SyntaxFactory.List(newMembers));
    }

    private static MemberDeclarationSyntax AddOverrideModifier(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.WithModifiers(ToOverrideModifiers(method.Modifiers)),
            PropertyDeclarationSyntax property => property.WithModifiers(ToOverrideModifiers(property.Modifiers)),
            _ => member
        };
    }

    private static SyntaxTokenList ToOverrideModifiers(SyntaxTokenList modifiers)
    {
        var tokens = StripModifiers(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.NewKeyword)
            .ToList();

        if (modifiers.Any(SyntaxKind.PrivateKeyword) || !HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
        return SyntaxFactory.TokenList(tokens);
    }

    private async Task<Solution> ApplyChangesAsync(
        Document derivedDocument,
        TypeDeclarationSyntax derivedDecl,
        TypeDeclarationSyntax newDerived,
        INamedTypeSymbol target,
        IReadOnlyList<MemberDeclarationSyntax> targetMembers,
        bool makeAbstract,
        CancellationToken cancellationToken)
    {
        var syntaxRef = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotEditable,
                $"Base type '{target.Name}' is not editable (defined in an external assembly).");
        }

        var targetSyntax = await syntaxRef.GetSyntaxAsync(cancellationToken) as TypeDeclarationSyntax;
        if (targetSyntax == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate declaration for '{target.Name}'.");
        }

        var newTarget = AddMembersToTarget(targetSyntax, targetMembers, makeAbstract && target.TypeKind != TypeKind.Interface);

        var solution = derivedDocument.Project.Solution;
        var targetDocument = solution.GetDocument(syntaxRef.SyntaxTree);
        if (targetDocument == null)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotEditable,
                $"Base type '{target.Name}' is not part of the workspace.");
        }

        if (targetDocument.Id == derivedDocument.Id)
        {
            var root = await derivedDocument.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var targetInRoot = FindTypeInRoot(root, target.Name, targetSyntax.Span);
            var updatedTarget = AddMembersToTarget(targetInRoot, targetMembers, makeAbstract && target.TypeKind != TypeKind.Interface);
            var newRoot = root.ReplaceNodes(
                new SyntaxNode[] { derivedDecl, targetInRoot },
                (original, _) => original == derivedDecl ? newDerived : updatedTarget);

            return derivedDocument.WithSyntaxRoot(newRoot).Project.Solution;
        }

        var derivedRoot = await derivedDocument.GetSyntaxRootAsync(cancellationToken);
        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (derivedRoot == null || targetRoot == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        solution = derivedDocument.WithSyntaxRoot(derivedRoot.ReplaceNode(derivedDecl, newDerived)).Project.Solution;
        targetDocument = solution.GetDocument(targetDocument.Id)!;
        targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (targetRoot == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse target file.");

        var currentTarget = FindTypeInRoot(targetRoot, target.Name, targetSyntax.Span);
        return targetDocument.WithSyntaxRoot(targetRoot.ReplaceNode(currentTarget, newTarget)).Project.Solution;
    }

    private static TypeDeclarationSyntax FindTypeInRoot(SyntaxNode root, string typeName, Microsoft.CodeAnalysis.Text.TextSpan preferredSpan)
    {
        var matches = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        return matches.FirstOrDefault(t => t.Span == preferredSpan) ?? matches[0];
    }

    private static TypeDeclarationSyntax AddMembersToTarget(
        TypeDeclarationSyntax target,
        IReadOnlyList<MemberDeclarationSyntax> members,
        bool makeClassAbstract)
    {
        var formatted = members.Select(member => member
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));

        var updated = target.WithMembers(target.Members.AddRange(formatted));

        if (makeClassAbstract &&
            target is ClassDeclarationSyntax &&
            !target.Modifiers.Any(SyntaxKind.AbstractKeyword))
        {
            updated = updated.AddModifiers(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        }

        return updated;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        PullMembersUpParams @params,
        INamedTypeSymbol target,
        IReadOnlyList<string> pulledNames,
        IReadOnlyList<MemberDeclarationSyntax> targetMembers,
        TypeDeclarationSyntax originalDerived,
        TypeDeclarationSyntax updatedDerived)
    {
        var memberList = string.Join(", ", pulledNames);
        var afterSnippet = string.Join(
            Environment.NewLine + Environment.NewLine,
            targetMembers.Select(m => m.NormalizeWhitespace().ToFullString()));

        var targetLocation = target.Locations.FirstOrDefault(l => l.IsInSource);
        var targetFile = targetLocation?.SourceTree?.FilePath ?? @params.SourceFile;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Add {memberList} to {@params.TargetBaseType ?? target.Name}",
                BeforeSnippet = $"{target.TypeKind.ToString().ToLowerInvariant()} {target.Name}",
                AfterSnippet = afterSnippet
            },
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = target.TypeKind == TypeKind.Interface
                    ? $"Keep {memberList} on {@params.TypeName} to implement {target.Name}"
                    : @params.MakeAbstract
                        ? $"Keep {memberList} on {@params.TypeName} as override"
                        : $"Remove {memberList} from {@params.TypeName}",
                BeforeSnippet = originalDerived.Identifier.Text,
                AfterSnippet = updatedDerived.NormalizeWhitespace().ToFullString()
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record PullableMember(string Name, ISymbol Symbol, MemberDeclarationSyntax Syntax);
}

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
/// Generates override methods for base class virtual/abstract members.
/// Honors <c>callBase</c> (default true) for ordinary methods
/// (<c>base.Method(...)</c>) and for non-abstract properties / indexers
/// (<c>return base.Prop;</c> / <c>base.Prop = value;</c>,
/// <c>return base[i];</c> / <c>base[i] = value;</c>). Abstract members
/// still throw. Honors <c>replaceExisting</c> to include already-overridden
/// members of this type, remove those override declarations (including
/// across partials) by signature, and insert a standard generated override.
/// <c>new</c> hiders, explicit interface implementations, non-override
/// methods, and primary constructors are never replaced. Extra modifiers
/// on the old override are not copied.
/// </summary>
public sealed class GenerateOverridesOperation : RefactoringOperationBase<GenerateOverridesParams>
{
    /// <summary>
    /// Creates a new generate overrides operation.
    /// </summary>
    public GenerateOverridesOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateOverridesParams @params) => Validate(@params);

    /// <summary>
    /// Validates generate-overrides parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(GenerateOverridesParams @params)
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
        GenerateOverridesParams @params,
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

        // Check for sealed class
        if (typeSymbol.IsSealed && typeSymbol.TypeKind != TypeKind.Struct)
        {
            // Sealed classes can still override, just can't be inherited from
        }

        var overridableMembers = CollectMembersToOverride(typeSymbol, @params.ReplaceExisting);

        // Filter to requested members if specified
        List<ISymbol> membersToOverride;
        if (@params.Members != null && @params.Members.Count > 0)
        {
            var requestedSet = new HashSet<string>(@params.Members, StringComparer.OrdinalIgnoreCase);
            membersToOverride = overridableMembers.Where(m => requestedSet.Contains(m.Name)).ToList();

            // Check for not found
            var foundNames = membersToOverride.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notFound = @params.Members.Where(n => !foundNames.Contains(n)).ToList();
            if (notFound.Count > 0)
            {
                throw new RefactoringException(
                    ErrorCodes.OverrideTargetNotFound,
                    $"Members not found or not overridable: {string.Join(", ", notFound)}. " +
                    $"Available: {string.Join(", ", overridableMembers.Select(m => m.Name))}");
            }
        }
        else
        {
            membersToOverride = overridableMembers;
        }

        if (membersToOverride.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoOverridableMembers,
                "No overridable members found in base classes.");
        }

        var replacements = ResolveReplacements(typeSymbol, membersToOverride, @params.ReplaceExisting);
        var membersToReplace = membersToOverride.Where(m => replacements.ContainsKey(m)).ToList();
        var membersToGenerate = membersToOverride.Where(m => !replacements.ContainsKey(m)).ToList();

        // Generate overrides
        var overrides = GenerateOverrideMembers(membersToOverride, @params.CallBase);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, membersToGenerate, membersToReplace, overrides, membersToOverride);
        }

        var solution = document.Project.Solution;
        if (replacements.Count > 0)
        {
            solution = await RemoveExistingOverridesAcrossPartialsAsync(
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

        // Add overrides to type
        var newTypeDeclaration = AddMembers(typeDeclaration, overrides);
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

    /// <summary>
    /// Missing overridable members (today's <see cref="MemberAnalyzer.GetOverridableMembers"/>
    /// plus Object ToString / Equals(object) / GetHashCode). When
    /// <paramref name="replaceExisting"/> is true, already-overridden members
    /// that generate_overrides would have emitted are added, and base members
    /// hidden by a <c>new</c> or other non-override same-signature member on
    /// this type are dropped.
    /// </summary>
    internal static List<ISymbol> CollectMembersToOverride(INamedTypeSymbol typeSymbol, bool replaceExisting)
    {
        var result = new List<ISymbol>();

        foreach (var member in MemberAnalyzer.GetOverridableMembers(typeSymbol))
            AddUnique(result, member);

        foreach (var member in GetObjectMethodsToOverride(typeSymbol))
            AddUnique(result, member);

        if (!replaceExisting)
            return result;

        foreach (var member in GetExistingOverrideTargets(typeSymbol))
            AddUnique(result, member);

        result.RemoveAll(m => IsHiddenByNonOverride(typeSymbol, m));
        return result;
    }

    private static void AddUnique(List<ISymbol> members, ISymbol member)
    {
        if (members.Any(existing => SignaturesMatch(existing, member)))
            return;

        members.Add(member);
    }

    private static List<ISymbol> GetObjectMethodsToOverride(INamedTypeSymbol typeSymbol)
    {
        var result = new List<ISymbol>();
        var existingOverrides = typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.IsOverride)
            .Select(m => m.Name)
            .ToHashSet();

        // Find Object type
        var objectType = typeSymbol.BaseType;
        while (objectType != null && objectType.SpecialType != SpecialType.System_Object)
        {
            objectType = objectType.BaseType;
        }

        if (objectType == null) return result;

        // Get ToString, Equals, GetHashCode from Object
        foreach (var member in objectType.GetMembers())
        {
            if (member is IMethodSymbol method &&
                (method.Name == "ToString" || method.Name == "Equals" || method.Name == "GetHashCode") &&
                method.IsVirtual &&
                !existingOverrides.Contains(method.Name))
            {
                // Skip Equals(object, object) static method
                if (method.Name == "Equals" && method.Parameters.Length != 1)
                    continue;

                result.Add(method);
            }
        }

        return result;
    }

    private static IEnumerable<ISymbol> GetExistingOverrideTargets(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            switch (member)
            {
                case IMethodSymbol method when IsEligibleExistingMethodOverride(method):
                    if (method.OverriddenMethod != null && IsGeneratedOverrideTarget(method.OverriddenMethod))
                        yield return method.OverriddenMethod;
                    break;
                case IPropertySymbol property when IsEligibleExistingPropertyOverride(property):
                    if (property.OverriddenProperty != null && IsGeneratedOverrideTarget(property.OverriddenProperty))
                        yield return property.OverriddenProperty;
                    break;
            }
        }
    }

    private static bool IsEligibleExistingMethodOverride(IMethodSymbol method) =>
        method.IsOverride
        && method.MethodKind == MethodKind.Ordinary
        && method.ExplicitInterfaceImplementations.Length == 0;

    private static bool IsEligibleExistingPropertyOverride(IPropertySymbol property) =>
        property.IsOverride
        && property.ExplicitInterfaceImplementations.Length == 0;

    private static bool IsEligibleExistingOverride(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => IsEligibleExistingMethodOverride(method),
            IPropertySymbol property => IsEligibleExistingPropertyOverride(property),
            _ => false
        };

    /// <summary>
    /// True when <paramref name="member"/> is a base member
    /// <c>generate_overrides</c> would emit: an overridable (unsealed
    /// virtual/abstract/override) ordinary method or property from a
    /// non-Object base, or Object ToString / Equals(object) / GetHashCode.
    /// </summary>
    private static bool IsGeneratedOverrideTarget(ISymbol member)
    {
        if (member is IMethodSymbol method)
        {
            var original = method;
            while (original.OverriddenMethod != null)
                original = original.OverriddenMethod;

            if (original.ContainingType?.SpecialType == SpecialType.System_Object)
            {
                return original.Name is "ToString" or "GetHashCode"
                    || (original.Name == "Equals" && original.Parameters.Length == 1 && !original.IsStatic);
            }

            return (original.IsVirtual || original.IsAbstract || original.IsOverride)
                && !original.IsSealed
                && original.MethodKind == MethodKind.Ordinary;
        }

        if (member is IPropertySymbol property)
        {
            var original = property;
            while (original.OverriddenProperty != null)
                original = original.OverriddenProperty;

            if (original.ContainingType?.SpecialType == SpecialType.System_Object)
                return false;

            return (original.IsVirtual || original.IsAbstract || original.IsOverride) && !original.IsSealed;
        }

        return false;
    }

    private static bool IsHiddenByNonOverride(INamedTypeSymbol typeSymbol, ISymbol baseMember)
    {
        foreach (var member in typeSymbol.GetMembers(baseMember.Name))
        {
            if (member.IsImplicitlyDeclared || member.IsOverride)
                continue;
            if (IsExplicitInterface(member))
                continue;
            if (SignaturesMatch(member, baseMember))
                return true;
        }

        return false;
    }

    private static bool IsExplicitInterface(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0
                || method.MethodKind == MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };

    /// <summary>
    /// Maps each selected member to the existing override on this type that
    /// will be removed. Exact signature match wins. Two same-name existing
    /// overrides with no exact match is <see cref="ErrorCodes.OverrideExists"/>.
    /// </summary>
    internal static Dictionary<ISymbol, ISymbol> ResolveReplacements(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<ISymbol> selectedMembers,
        bool replaceExisting)
    {
        var replacements = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
        if (!replaceExisting)
            return replacements;

        foreach (var selected in selectedMembers)
        {
            var existing = FindExistingOverride(typeSymbol, selected, out var ambiguous);
            if (ambiguous)
            {
                throw new RefactoringException(
                    ErrorCodes.OverrideExists,
                    $"Multiple existing overrides named '{selected.Name}' and none matches the selected signature.");
            }

            if (existing != null)
                replacements[selected] = existing;
        }

        return replacements;
    }

    private static ISymbol? FindExistingOverride(
        INamedTypeSymbol typeSymbol,
        ISymbol selected,
        out bool ambiguous)
    {
        ambiguous = false;
        var sameName = new List<ISymbol>();
        ISymbol? exact = null;

        foreach (var member in typeSymbol.GetMembers())
        {
            if (!IsEligibleExistingOverride(member))
                continue;
            if (!string.Equals(member.Name, selected.Name, StringComparison.Ordinal))
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

    internal static bool SignaturesMatch(ISymbol left, ISymbol right)
    {
        if (left is IMethodSymbol leftMethod && right is IMethodSymbol rightMethod)
            return MethodSignaturesMatch(leftMethod, rightMethod);

        if (left is IPropertySymbol leftProp && right is IPropertySymbol rightProp)
            return PropertySignaturesMatch(leftProp, rightProp);

        return false;
    }

    private static bool MethodSignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal))
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
    /// Method type parameters are distinct symbols on the base vs override
    /// (<c>Base.M&lt;T&gt;(T)</c> vs <c>Derived.M&lt;T&gt;(T)</c>), so
    /// <see cref="SymbolEqualityComparer.Default"/> misses an exact match.
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
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            return false;
        if (left.IsIndexer != right.IsIndexer)
            return false;
        if (!left.IsIndexer)
            return true;
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

    private static List<MemberDeclarationSyntax> GenerateOverrideMembers(
        List<ISymbol> members,
        bool callBase)
    {
        var overrides = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? impl = member switch
            {
                IMethodSymbol method => SyntaxGenerationHelper.CreateMethodStub(
                    method,
                    explicitInterface: false,
                    callBase: callBase && !method.IsAbstract,
                    throwNotImplemented: method.IsAbstract),
                IPropertySymbol { IsIndexer: true } indexer => SyntaxGenerationHelper.CreateIndexerStub(
                    indexer,
                    explicitInterface: false,
                    throwNotImplemented: indexer.IsAbstract,
                    callBase: callBase && !indexer.IsAbstract),
                IPropertySymbol property => SyntaxGenerationHelper.CreatePropertyStub(
                    property,
                    explicitInterface: false,
                    throwNotImplemented: property.IsAbstract,
                    callBase: callBase && !property.IsAbstract),
                _ => null
            };

            if (impl != null)
            {
                overrides.Add(impl);
            }
        }

        return overrides;
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

    /// <summary>
    /// Removes matched override declarations from every partial that holds
    /// them. Match by span/kind, not SyntaxNode reference — same seam as
    /// constructor / Equals / ToString replaceExisting.
    /// </summary>
    private static async Task<Solution> RemoveExistingOverridesAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IEnumerable<ISymbol> existingOverrides,
        CancellationToken cancellationToken)
    {
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();

        foreach (var existing in existingOverrides)
        {
            foreach (var reference in existing.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (syntax.Parent is not TypeDeclarationSyntax part)
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

                keys.Add((syntax.SpanStart, syntax.Span.End, syntax.Kind()));
            }
        }

        foreach (var (tree, byPart) in membersByTreeAndPart)
        {
            var document = solution.GetDocument(tree)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate a declaring document for type '{typeSymbol.Name}'.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var replacements = new Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree != tree)
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax part)
                    continue;
                if (!byPart.TryGetValue(part.SpanStart, out var keys) || keys.Count == 0)
                    continue;

                var remainingMembers = part.Members
                    .Where(m => !keys.Contains((m.SpanStart, m.Span.End, m.Kind())))
                    .ToArray();
                replacements[part] = part.WithMembers(SyntaxFactory.List(remainingMembers));
            }

            if (replacements.Count == 0)
                continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    private static TypeDeclarationSyntax? FindTypeDeclaration(SyntaxNode root, string typeName, int preferredSpanStart)
    {
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();
        return matches.FirstOrDefault(t => t.SpanStart == preferredSpanStart) ?? matches.FirstOrDefault();
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        GenerateOverridesParams @params,
        List<ISymbol> membersToGenerate,
        List<ISymbol> membersToReplace,
        List<MemberDeclarationSyntax> overrides,
        List<ISymbol> selectedMembers)
    {
        var description = BuildPreviewDescription(membersToGenerate, membersToReplace, @params.CallBase, selectedMembers);
        var overrideCode = string.Join("\n\n",
            overrides.Select(o => o.NormalizeWhitespace().ToFullString()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = description,
                BeforeSnippet = membersToReplace.Count > 0
                    ? $"// Type '{@params.TypeName}' (replacing existing overrides)"
                    : $"// End of type '{@params.TypeName}'",
                AfterSnippet = overrideCode
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    internal static string BuildPreviewDescription(
        IReadOnlyList<ISymbol> membersToGenerate,
        IReadOnlyList<ISymbol> membersToReplace,
        bool callBase = true,
        IReadOnlyList<ISymbol>? selectedMembers = null)
    {
        var generated = string.Join(", ", membersToGenerate.Select(m => m.Name));
        var replaced = string.Join(", ", membersToReplace.Select(m => m.Name));

        string description;
        if (membersToReplace.Count == 0)
            description = $"Generate overrides for: {generated}";
        else if (membersToGenerate.Count == 0)
            description = $"Replace existing overrides: {replaced}";
        else
            description = $"Generate overrides for: {generated}; replace existing overrides: {replaced}";

        var properties = (selectedMembers ?? membersToGenerate.Concat(membersToReplace))
            .OfType<IPropertySymbol>()
            .ToList();
        if (properties.Count > 0)
        {
            var anyNonAbstract = properties.Any(p => !p.IsAbstract);
            var anyAbstract = properties.Any(p => p.IsAbstract);
            if (!callBase || !anyNonAbstract)
                description += "; property accessors will not call base";
            else if (anyAbstract)
                description += "; non-abstract property accessors will call base";
            else
                description += "; property accessors will call base";
        }

        return description;
    }
}

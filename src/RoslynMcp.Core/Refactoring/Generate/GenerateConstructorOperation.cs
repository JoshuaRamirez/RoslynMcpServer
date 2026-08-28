using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates a constructor that initializes fields and/or properties.
/// Honors <c>includeProperties</c> when auto-collecting members: omitted / true
/// keeps today's field + settable-property set; false uses instance fields only
/// unless <c>members</c> names a property (named resolution still considers
/// fields and settable properties).
/// Honors <c>includeInheritedMembers</c> to append accessible base-type members
/// (settable properties, not the readable-property equality collector),
/// <c>replaceExisting</c> to remove an existing non-implicit constructor
/// with the exact same signature (count, types, and RefKind) before generating
/// a fresh one, <c>visibility</c> for the generated constructor's
/// accessibility (omitted / public keeps today's public constructor),
/// and <c>copyConstructor</c> to emit a single same-type parameter whose
/// body assigns each selected member from that parameter instead of one
/// parameter per member. Derived records whose base is also a record get
/// <c>: base(other)</c> (CS8868). Ordinary classes do not unless
/// <c>classBaseCopy</c> is also true; then they emit
/// <c>: base((Base)other)</c> when the immediate base has an
/// accessible copy constructor of the base type. Unsealed record
/// copy constructors reject visibilities other than public / protected
/// (CS8878). Copy mode skips setter-only / unreadable properties.
/// Structs and record structs reject <c>protected</c> /
/// <c>protected internal</c> / <c>private protected</c> (CS0666) before any
/// generate, replace, or preview write. Primary constructors are not replaced.
/// </summary>
public sealed class GenerateConstructorOperation : RefactoringOperationBase<GenerateConstructorParams>
{
    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "private", "protected", "internal", "protected internal", "private protected"
    };

    private static readonly HashSet<string> ProtectedVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "protected", "protected internal", "private protected"
    };

    private static readonly string[] CopyParameterNameCandidates = ["other", "source", "original"];

    /// <summary>
    /// CS8878: an unsealed record copy constructor must be public or protected.
    /// </summary>
    private static readonly HashSet<string> UnsealedRecordCopyVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "protected"
    };

    /// <summary>
    /// Creates a new generate constructor operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public GenerateConstructorOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateConstructorParams @params) => Validate(@params);

    /// <summary>
    /// Validates generate-constructor parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(GenerateConstructorParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!string.IsNullOrWhiteSpace(@params.Visibility) && !ValidVisibilities.Contains(@params.Visibility.Trim()))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"Invalid visibility: {@params.Visibility}");

        if (@params.ClassBaseCopy && !@params.CopyConstructor)
        {
            throw new RefactoringException(
                ErrorCodes.ClassBaseCopyRequiresCopyConstructor,
                "classBaseCopy requires copyConstructor to be true.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        GenerateConstructorParams @params,
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

        // Check for static class
        if (typeSymbol.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.TypeIsStatic,
                "Cannot add constructor to static class.");
        }

        // C# forbids protected members on structs / record structs (CS0666).
        // Reject before member collection, generation, replaceExisting, or preview.
        var visibility = ResolveVisibility(@params.Visibility);
        if (typeSymbol.TypeKind == TypeKind.Struct && ProtectedVisibilities.Contains(visibility))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidVisibility,
                $"Cannot generate a {visibility} constructor on struct '{@params.TypeName}'. C# does not allow protected members on structs (CS0666).");
        }

        // CS8878: a copy constructor on an unsealed record must be public or protected.
        // Sealed records and record structs (implicitly sealed) may use other visibilities.
        if (@params.CopyConstructor &&
            typeSymbol.IsRecord &&
            !typeSymbol.IsSealed &&
            !UnsealedRecordCopyVisibilities.Contains(visibility))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidVisibility,
                $"Cannot generate a {visibility} copy constructor on unsealed record '{@params.TypeName}'. C# requires the copy constructor to be public or protected (CS8878).");
        }

        // Get fields and properties to initialize
        var members = GetMembersToInitialize(
            typeSymbol,
            @params.Members,
            @params.IncludeProperties,
            @params.IncludeInheritedMembers,
            @params.CopyConstructor);

        if (members.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                "No members found to initialize in constructor.");
        }

        // Check for existing constructor with same signature or ambiguous due to optional params.
        // replaceExisting only lifts the exact-signature reject (same count/types/RefKind in order)
        // for a non-primary constructor; optional-param / required-param ambiguity still throws
        // — do not guess an overload. Primary constructors cannot be removed (their declaring
        // syntax is the type parameter list), so they stay ConstructorExists even when replacing.
        // Copy constructors compare against exactly one by-value parameter of the target type.
        var parameterTypes = @params.CopyConstructor
            ? new List<ITypeSymbol> { typeSymbol }
            : members.Select(GetMemberType).ToList();
        var newParamCount = parameterTypes.Count;
        var copyParameterName = @params.CopyConstructor ? ChooseCopyParameterName(typeSymbol) : null;
        IMethodSymbol? exactMatch = null;

        foreach (var ctor in typeSymbol.Constructors.Where(c => !c.IsImplicitlyDeclared))
        {
            // Exact signature match
            if (HasExactSignature(ctor, parameterTypes))
            {
                if (@params.ReplaceExisting && !IsPrimaryConstructor(ctor))
                {
                    exactMatch = ctor;
                    continue;
                }

                throw new RefactoringException(
                    ErrorCodes.ConstructorExists,
                    "A constructor with the same signature already exists.");
            }

            // Check for ambiguity with optional parameters
            // Case 1: New constructor could be called where existing has optional params
            var requiredParams = ctor.Parameters.TakeWhile(p => !p.IsOptional).ToList();

            if (requiredParams.Count <= newParamCount && ctor.Parameters.Length >= newParamCount)
            {
                // Check if first N types and RefKinds match (where N is new param count)
                if (ParametersMatchGeneratedSignature(ctor.Parameters.Take(newParamCount), parameterTypes))
                {
                    throw new RefactoringException(
                        ErrorCodes.ConstructorExists,
                        $"New constructor would be ambiguous with existing constructor that has optional parameters.");
                }
            }

            // Case 2: Existing constructor with fewer params could match if new constructor adds optionals
            if (requiredParams.Count == newParamCount &&
                ParametersMatchGeneratedSignature(requiredParams, parameterTypes))
            {
                throw new RefactoringException(
                    ErrorCodes.ConstructorExists,
                    $"New constructor would conflict with existing constructor's required parameters.");
            }
        }

        // Class : base((Base)other) is opt-in. Records already chain (CS8868)
        // and ignore classBaseCopy. Only an ordinary class whose immediate
        // base has an accessible Base(Base) copy constructor gets the
        // initializer; the argument is cast to the base type so that ctor wins.
        var addClassBaseCopy = @params.CopyConstructor
            && @params.ClassBaseCopy
            && TryGetClassBaseCopyInitializer(typeSymbol);

        var bodyMembers = addClassBaseCopy
            ? members.Where(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, typeSymbol)).ToList()
            : members;

        // Generate the constructor. Visibility is always the requested
        // accessibility (default public) — never copied from a replaced ctor.
        var constructor = @params.CopyConstructor
            ? GenerateCopyConstructor(
                bodyMembers,
                typeDeclaration,
                typeSymbol,
                copyParameterName!,
                @params.AddNullChecks && typeSymbol.IsReferenceType,
                visibility,
                addClassBaseCopy)
            : GenerateConstructor(members, typeDeclaration, @params.AddNullChecks, visibility);
        var replacing = exactMatch != null;

        // If preview mode, return without applying (but include generated constructor code)
        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId, @params, bodyMembers, constructor, exactMatch, document.Project.Solution, copyParameterName, addClassBaseCopy, cancellationToken);
        }

        var solution = document.Project.Solution;
        if (replacing)
        {
            solution = await RemoveExistingConstructorAcrossPartialsAsync(
                solution, typeSymbol, exactMatch!, cancellationToken);
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

        // Add constructor to type
        var newTypeDeclaration = InsertConstructor(typeDeclaration, constructor);
        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);

        var newDocument = document.WithSyntaxRoot(newRoot);

        // Commit changes
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

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
                FullyQualifiedName = @params.TypeName,
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private static List<ISymbol> GetMembersToInitialize(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<string>? requestedMembers,
        bool includeProperties,
        bool includeInheritedMembers,
        bool copyConstructor)
    {
        var allMembers = new List<ISymbol>();
        var hasRequestedMembers = requestedMembers != null && requestedMembers.Count > 0;

        CollectDeclaredMembers(
            typeSymbol, typeSymbol, allMembers, includeProperties, hasRequestedMembers, requireAccessible: false);

        if (includeInheritedMembers)
        {
            for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (IsObjectOrValueType(baseType))
                    break;

                CollectDeclaredMembers(
                    baseType, typeSymbol, allMembers, includeProperties, hasRequestedMembers, requireAccessible: true);
            }
        }

        if (hasRequestedMembers)
        {
            // Filter to only requested members
            var requestedSet = new HashSet<string>(requestedMembers!);
            allMembers = allMembers.Where(m => requestedSet.Contains(m.Name)).ToList();

            // Validate all requested members were found
            var foundNames = allMembers.Select(m => m.Name).ToHashSet();
            var notFound = requestedMembers!.Where(n => !foundNames.Contains(n)).ToList();
            if (notFound.Count > 0)
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotFound,
                    $"Members not found: {string.Join(", ", notFound)}");
            }

            // Copy mode reads other.Member — a named setter-only / unreadable
            // property is not a silent skip; fail with MemberNotFound.
            if (copyConstructor)
            {
                var unreadable = allMembers
                    .Where(m => !IsReadableInCopyConstructor(m, typeSymbol))
                    .Select(m => m.Name)
                    .ToList();
                if (unreadable.Count > 0)
                {
                    throw new RefactoringException(
                        ErrorCodes.MemberNotFound,
                        $"Members not readable in copy constructor: {string.Join(", ", unreadable)}");
                }
            }
        }
        else if (copyConstructor)
        {
            // Auto-collection: skip properties that cannot be read as other.Member.
            allMembers = allMembers.Where(m => IsReadableInCopyConstructor(m, typeSymbol)).ToList();
        }

        return allMembers;
    }

    /// <summary>
    /// Collects instance fields and, when requested, settable properties declared
    /// on <paramref name="declaringType"/>. Indexers are never collected — there
    /// are no index arguments for a constructor assignment. When
    /// <paramref name="requireAccessible"/> is true (inherited members), only
    /// members visible from <paramref name="fromType"/> are added, hidden/overridden
    /// names are skipped, and inherited readonly fields are omitted because a
    /// derived constructor cannot assign them.
    /// </summary>
    private static void CollectDeclaredMembers(
        INamedTypeSymbol declaringType,
        INamedTypeSymbol fromType,
        List<ISymbol> members,
        bool includeProperties,
        bool hasRequestedMembers,
        bool requireAccessible)
    {
        foreach (var field in declaringType.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
                continue;
            // Inherited readonly fields cannot be assigned in a derived constructor (CS0191).
            if (requireAccessible && field.IsReadOnly)
                continue;
            if (requireAccessible && !IsAccessibleFrom(field, fromType))
                continue;
            if (requireAccessible && IsHiddenFrom(field, fromType))
                continue;
            members.Add(field);
        }

        // Auto-collection includes settable properties only when includeProperties is true.
        // A non-empty members list is authoritative and still resolves against settable
        // properties even if includeProperties is false (same rule as equals/tostring).
        if (includeProperties || hasRequestedMembers)
        {
            foreach (var prop in declaringType.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsReadOnly || prop.SetMethod == null || prop.IsImplicitlyDeclared || prop.IsIndexer)
                    continue;
                if (requireAccessible && !IsAccessibleFrom(prop, fromType))
                    continue;
                if (requireAccessible && IsHiddenFrom(prop, fromType))
                    continue;
                members.Add(prop);
            }
        }
    }

    private static bool IsObjectOrValueType(INamedTypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;

    /// <summary>
    /// True when a closer type hides or overrides <paramref name="member"/> so
    /// <c>this.Name</c> would bind to that closer member (or fail to compile)
    /// instead of the inherited one. Any non-implicit closer member with the
    /// same name counts as a hider, including methods and nested types.
    /// Implicit members (for example auto-property backing fields) are ignored.
    /// </summary>
    private static bool IsHiddenFrom(ISymbol member, INamedTypeSymbol fromType)
    {
        var declaring = member.ContainingType;
        for (var current = fromType;
             current != null && !SymbolEqualityComparer.Default.Equals(current, declaring);
             current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(member.Name))
            {
                if (candidate.IsImplicitlyDeclared)
                    continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="member"/> can be assigned as <c>this.Name</c> from
    /// <paramref name="fromType"/> (public / protected / protected-internal;
    /// internal and private-protected when the same assembly). For properties the
    /// setter accessibility is used (constructor initialization, not read).
    /// </summary>
    private static bool IsAccessibleFrom(ISymbol member, INamedTypeSymbol fromType)
    {
        var accessibility = member.DeclaredAccessibility;
        if (member is IPropertySymbol { SetMethod: { } setter })
            accessibility = MoreRestrictive(accessibility, setter.DeclaredAccessibility);

        return accessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => SameAssembly(member, fromType),
            Accessibility.ProtectedAndInternal => SameAssembly(member, fromType),
            _ => false
        };
    }

    private static Accessibility MoreRestrictive(Accessibility left, Accessibility right) =>
        AccessibilityRank(left) <= AccessibilityRank(right) ? left : right;

    private static int AccessibilityRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => 0,
        Accessibility.ProtectedAndInternal => 1,
        Accessibility.Internal => 2,
        Accessibility.Protected => 3,
        Accessibility.ProtectedOrInternal => 4,
        Accessibility.Public => 5,
        _ => 0
    };

    private static bool SameAssembly(ISymbol member, INamedTypeSymbol fromType) =>
        SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, fromType.ContainingAssembly);

    /// <summary>
    /// Copy mode emits <c>other.Member</c>, so the member must be readable.
    /// Fields already collected are readable. Properties need a getter; inherited
    /// getters must be accessible from <paramref name="fromType"/>.
    /// </summary>
    private static bool IsReadableInCopyConstructor(ISymbol member, INamedTypeSymbol fromType)
    {
        if (member is IFieldSymbol)
            return true;

        if (member is not IPropertySymbol property || property.GetMethod == null)
            return false;

        if (SymbolEqualityComparer.Default.Equals(property.ContainingType, fromType))
            return true;

        return IsAccessorAccessibleFrom(property.GetMethod, fromType);
    }

    private static bool IsAccessorAccessibleFrom(IMethodSymbol accessor, INamedTypeSymbol fromType) =>
        accessor.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => SameAssembly(accessor, fromType),
            Accessibility.ProtectedAndInternal => SameAssembly(accessor, fromType),
            _ => false
        };

    /// <summary>
    /// True when <paramref name="constructor"/> has the same parameter count, types
    /// (in order, via <see cref="SymbolEqualityComparer"/>), and <see cref="RefKind"/>
    /// as the constructor we would generate (all generated parameters are by-value).
    /// <c>ref</c> / <c>out</c> / <c>in</c> overloads are distinct and must not be
    /// treated as replaceable exact matches.
    /// </summary>
    private static bool HasExactSignature(IMethodSymbol constructor, IReadOnlyList<ITypeSymbol> parameterTypes) =>
        constructor.Parameters.Length == parameterTypes.Count &&
        ParametersMatchGeneratedSignature(constructor.Parameters, parameterTypes);

    /// <summary>
    /// Generated constructors are always by-value (<see cref="RefKind.None"/>).
    /// Types and RefKinds must both match — <c>Widget(ref string)</c> is not
    /// <c>Widget(string)</c>.
    /// </summary>
    private static bool ParametersMatchGeneratedSignature(
        IEnumerable<IParameterSymbol> parameters,
        IReadOnlyList<ITypeSymbol> expectedTypes)
    {
        var list = parameters as IReadOnlyList<IParameterSymbol> ?? parameters.ToList();
        if (list.Count != expectedTypes.Count)
            return false;

        for (var i = 0; i < expectedTypes.Count; i++)
        {
            if (list[i].RefKind != RefKind.None)
                return false;
            if (!SymbolEqualityComparer.Default.Equals(list[i].Type, expectedTypes[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Primary constructors are declared as the type's parameter list, not a
    /// <see cref="ConstructorDeclarationSyntax"/>. Removal would skip them and
    /// still insert an explicit constructor (duplicate signatures).
    /// </summary>
    private static bool IsPrimaryConstructor(IMethodSymbol constructor)
    {
        foreach (var reference in constructor.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            if (syntax is ConstructorDeclarationSyntax)
                return false;
            if (syntax is ParameterListSyntax)
                return true;
            if (syntax.Parent is ParameterListSyntax)
                return true;
            if (syntax is TypeDeclarationSyntax { ParameterList: not null })
                return true;
        }

        return false;
    }

    /// <summary>
    /// Removes the exact-signature constructor declaration from every partial
    /// that holds it. Match by span/kind, not SyntaxNode reference — same seam
    /// as Equals / ToString replaceExisting.
    /// </summary>
    private static async Task<Solution> RemoveExistingConstructorAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IMethodSymbol constructor,
        CancellationToken cancellationToken)
    {
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();

        foreach (var reference in constructor.DeclaringSyntaxReferences)
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

    private static ITypeSymbol GetMemberType(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new InvalidOperationException($"Unexpected member type: {member.GetType()}")
        };
    }

    private static string ResolveVisibility(string? visibility) =>
        string.IsNullOrWhiteSpace(visibility) ? "public" : visibility.Trim();

    private static ConstructorDeclarationSyntax GenerateConstructor(
        List<ISymbol> members,
        TypeDeclarationSyntax typeDeclaration,
        bool addNullChecks,
        string visibility)
    {
        // Build parameters
        var parameters = new List<ParameterSyntax>();
        foreach (var member in members)
        {
            var type = GetMemberType(member);
            var paramName = ToCamelCase(member.Name);

            var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                .WithType(SyntaxFactory.ParseTypeName(type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space));

            parameters.Add(parameter);
        }

        // Build body statements
        var statements = new List<StatementSyntax>();

        foreach (var member in members)
        {
            var paramName = ToCamelCase(member.Name);
            var memberType = GetMemberType(member);

            // Add null check if requested and type can be null.
            // Null checks are generated for:
            // - Reference types that are not nullable (e.g., string, not string?)
            // - Nullable<T> value types (e.g., int?) since they can hold null
            // Null checks are NOT generated for:
            // - Non-nullable value types (e.g., int, bool) - cannot be null
            // - Nullable-annotated reference types (e.g., string?) - null is expected
            if (addNullChecks && ShouldGenerateNullCheck(memberType))
                statements.Add(CreateArgumentNullCheck(paramName));

            // Assignment statement
            ExpressionSyntax left;
            if (member.Name == paramName || member.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
            {
                // Need to disambiguate with "this."
                left = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ThisExpression(),
                    SyntaxFactory.IdentifierName(member.Name));
            }
            else
            {
                left = SyntaxFactory.IdentifierName(member.Name);
            }

            var assignment = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    left,
                    SyntaxFactory.IdentifierName(paramName)));

            statements.Add(assignment);
        }

        // Build constructor
        var constructor = SyntaxFactory.ConstructorDeclaration(typeDeclaration.Identifier)
            .WithModifiers(SyntaxFactory.TokenList(ParseVisibilityTokens(visibility)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block(statements))
            .NormalizeWhitespace();

        return constructor;
    }

    /// <summary>
    /// Single-parameter copy constructor: <c>TypeName(TypeName other)</c> with
    /// <c>this.Member = other.Member;</c> for each selected member. Parameter
    /// name is the first of <c>other</c> / <c>source</c> / <c>original</c>
    /// that does not collide with a member or type parameter on the type.
    /// Null-check the copy parameter only when requested and the target is a
    /// reference type — structs / record structs skip it.
    /// Derived records whose base is also a record get
    /// <c>: base(other)</c> (CS8868). Ordinary classes get
    /// <c>: base((Base)other)</c> only when <paramref name="addClassBaseCopy"/>
    /// is true.
    /// </summary>
    private static ConstructorDeclarationSyntax GenerateCopyConstructor(
        List<ISymbol> members,
        TypeDeclarationSyntax typeDeclaration,
        INamedTypeSymbol typeSymbol,
        string parameterName,
        bool addNullCheckOnParameter,
        string visibility,
        bool addClassBaseCopy)
    {
        var selfTypeName = GetSelfTypeName(typeDeclaration);
        var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameterName))
            .WithType(SyntaxFactory.ParseTypeName(selfTypeName).WithTrailingTrivia(SyntaxFactory.Space));

        var statements = new List<StatementSyntax>();

        if (addNullCheckOnParameter)
            statements.Add(CreateArgumentNullCheck(parameterName));

        foreach (var member in members)
        {
            var left = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ThisExpression(),
                SyntaxFactory.IdentifierName(member.Name));
            var right = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(parameterName),
                SyntaxFactory.IdentifierName(member.Name));

            statements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    left,
                    right)));
        }

        var constructor = SyntaxFactory.ConstructorDeclaration(typeDeclaration.Identifier)
            .WithModifiers(SyntaxFactory.TokenList(ParseVisibilityTokens(visibility)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(parameter)))
            .WithBody(SyntaxFactory.Block(statements));

        // Record language rule (CS8868) keeps : base(other). Class base-copy
        // casts to the immediate base type so Base(Base) wins over Base(IFoo)
        // / a more-specific inaccessible Base(Derived).
        if (RequiresRecordBaseCopyInitializer(typeSymbol) || addClassBaseCopy)
        {
            constructor = constructor.WithInitializer(
                SyntaxFactory.ConstructorInitializer(
                    SyntaxKind.BaseConstructorInitializer,
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                CreateBaseCopyInitializerArgument(
                                    typeSymbol, parameterName, addClassBaseCopy))))));
        }

        return constructor.NormalizeWhitespace();
    }

    /// <summary>
    /// True when the target is a record whose immediate base is also a record
    /// (not <c>object</c> / <c>ValueType</c>). The generated copy constructor
    /// must invoke the base copy constructor.
    /// </summary>
    private static bool RequiresRecordBaseCopyInitializer(INamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.IsRecord)
            return false;

        var baseType = typeSymbol.BaseType;
        if (baseType == null || IsObjectOrValueType(baseType))
            return false;

        return baseType.IsRecord;
    }

    /// <summary>
    /// Record copy constructors pass the copy parameter through unchanged
    /// (<c>: base(other)</c>, CS8868). Class <c>classBaseCopy</c> casts to
    /// the immediate base type (<c>: base((Base)other)</c>) so the call
    /// binds to the validated <c>Base(Base)</c> constructor.
    /// </summary>
    private static ExpressionSyntax CreateBaseCopyInitializerArgument(
        INamedTypeSymbol typeSymbol,
        string parameterName,
        bool addClassBaseCopy)
    {
        var parameter = SyntaxFactory.IdentifierName(parameterName);
        if (!addClassBaseCopy || typeSymbol.BaseType == null)
            return parameter;

        var baseTypeName = typeSymbol.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return SyntaxFactory.CastExpression(
            SyntaxFactory.ParseTypeName(baseTypeName),
            parameter);
    }

    /// <summary>
    /// True when the target is an ordinary class (not a record / struct /
    /// record struct) whose immediate base is a class other than
    /// <c>object</c> and that base has an accessible instance constructor
    /// with exactly one by-value parameter of the <em>base</em> type.
    /// Public / protected / protected-internal always count; internal and
    /// private-protected count when the same assembly. Private and
    /// otherwise-inaccessible constructors do not. A record / struct base
    /// does not qualify — classBaseCopy is a no-op in those cases.
    /// </summary>
    private static bool TryGetClassBaseCopyInitializer(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.IsRecord || typeSymbol.TypeKind != TypeKind.Class)
            return false;

        var baseType = typeSymbol.BaseType;
        if (baseType == null || IsObjectOrValueType(baseType))
            return false;
        if (baseType.IsRecord || baseType.TypeKind != TypeKind.Class)
            return false;

        foreach (var constructor in baseType.InstanceConstructors)
        {
            if (constructor.Parameters.Length != 1)
                continue;

            var parameter = constructor.Parameters[0];
            if (parameter.RefKind != RefKind.None)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(parameter.Type, baseType))
                continue;
            if (!IsConstructorAccessibleFrom(constructor, typeSymbol))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Accessibility of a base constructor from a derived type: public /
    /// protected / protected-internal, plus internal and private-protected
    /// when the same assembly.
    /// </summary>
    private static bool IsConstructorAccessibleFrom(IMethodSymbol constructor, INamedTypeSymbol fromType) =>
        constructor.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => SameAssembly(constructor, fromType),
            Accessibility.ProtectedAndInternal => SameAssembly(constructor, fromType),
            _ => false
        };

    /// <summary>
    /// Prefer <c>other</c>, then <c>source</c>, then <c>original</c> — first
    /// identifier that is not already a member or type parameter on the type.
    /// If all three collide, append a numeric suffix to <c>other</c>.
    /// </summary>
    private static string ChooseCopyParameterName(INamedTypeSymbol typeSymbol)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;
            taken.Add(member.Name);
        }

        foreach (var typeParameter in typeSymbol.TypeParameters)
            taken.Add(typeParameter.Name);

        foreach (var candidate in CopyParameterNameCandidates)
        {
            if (!taken.Contains(candidate))
                return candidate;
        }

        var suffix = 1;
        while (taken.Contains($"other{suffix}"))
            suffix++;
        return $"other{suffix}";
    }

    /// <summary>
    /// Same constructed self-type spelling as generate_equals_hashcode
    /// (<c>Widget</c>, <c>Box&lt;T&gt;</c>).
    /// </summary>
    private static string GetSelfTypeName(TypeDeclarationSyntax typeDecl)
    {
        var identifier = typeDecl.Identifier.Text;
        if (typeDecl.TypeParameterList == null || typeDecl.TypeParameterList.Parameters.Count == 0)
            return identifier;

        var arguments = string.Join(", ", typeDecl.TypeParameterList.Parameters.Select(p => p.Identifier.Text));
        return $"{identifier}<{arguments}>";
    }

    private static IfStatementSyntax CreateArgumentNullCheck(string paramName)
    {
        return SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName(paramName),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.IdentifierName("ArgumentNullException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.IdentifierName("nameof"))
                            .WithArgumentList(SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(
                                        SyntaxFactory.IdentifierName(paramName)))))))))));
    }

    /// <summary>
    /// Same token split as generate_property / generate_method_stub: one token
    /// per whitespace-separated keyword so <c>protected internal</c> and
    /// <c>private protected</c> emit both modifiers.
    /// </summary>
    private static IEnumerable<SyntaxToken> ParseVisibilityTokens(string visibility)
    {
        var tokens = visibility
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseVisibilityKeyword)
            .ToList();

        if (tokens.Count == 0)
            tokens.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        return tokens;
    }

    private static SyntaxToken ParseVisibilityKeyword(string keyword) => keyword.ToLowerInvariant() switch
    {
        "public" => SyntaxFactory.Token(SyntaxKind.PublicKeyword),
        "private" => SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
        "protected" => SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
        "internal" => SyntaxFactory.Token(SyntaxKind.InternalKeyword),
        _ => SyntaxFactory.Token(SyntaxKind.PublicKeyword)
    };

    private static TypeDeclarationSyntax InsertConstructor(
        TypeDeclarationSyntax typeDeclaration,
        ConstructorDeclarationSyntax constructor)
    {
        // Find insertion point: after fields, before methods
        var members = typeDeclaration.Members.ToList();
        var insertIndex = 0;

        // Find last field or existing constructor
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is FieldDeclarationSyntax ||
                members[i] is ConstructorDeclarationSyntax)
            {
                insertIndex = i + 1;
            }
        }

        members.Insert(insertIndex, constructor
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    /// <summary>
    /// Converts a name to camelCase following common C# conventions.
    /// </summary>
    /// <param name="name">The name to convert.</param>
    /// <returns>The camelCase version of the name.</returns>
    /// <remarks>
    /// Handles edge cases:
    /// <list type="bullet">
    ///   <item>Leading underscores: _foo -> foo, __bar -> bar</item>
    ///   <item>Single character after underscore: _X -> x</item>
    ///   <item>All caps: URL -> url, HTTP -> http</item>
    ///   <item>All caps with trailing lowercase: HTTPClient -> httpClient</item>
    ///   <item>Empty/null strings: returns as-is</item>
    /// </list>
    /// </remarks>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Remove all leading underscores
        var startIndex = 0;
        while (startIndex < name.Length && name[startIndex] == '_')
        {
            startIndex++;
        }

        // If all underscores or empty after stripping, return a default
        if (startIndex >= name.Length)
        {
            return "value";
        }

        name = name.Substring(startIndex);

        // Handle empty result after underscore removal
        if (name.Length == 0)
        {
            return "value";
        }

        // Handle single character
        if (name.Length == 1)
        {
            return char.ToLowerInvariant(name[0]).ToString();
        }

        // Handle all caps: URL -> url, HTTP -> http
        // Also handles all caps with trailing lowercase: HTTPClient -> httpClient
        if (char.IsUpper(name[0]))
        {
            // Count consecutive uppercase characters
            var upperCount = 0;
            while (upperCount < name.Length && char.IsUpper(name[upperCount]))
            {
                upperCount++;
            }

            if (upperCount == name.Length)
            {
                // Entire string is uppercase: URL -> url
                return name.ToLowerInvariant();
            }
            else if (upperCount > 1)
            {
                // Multiple uppercase followed by lowercase: HTTPClient -> httpClient
                // Keep the last uppercase as the start of the next word
                return name.Substring(0, upperCount - 1).ToLowerInvariant() +
                       name.Substring(upperCount - 1);
            }
            else
            {
                // Single uppercase at start: MyProperty -> myProperty
                return char.ToLowerInvariant(name[0]) + name.Substring(1);
            }
        }

        return name;
    }

    /// <summary>
    /// Determines whether a null check should be generated for a given type.
    /// </summary>
    /// <param name="type">The type to evaluate.</param>
    /// <returns>
    /// True if a null check should be generated; false otherwise.
    /// </returns>
    /// <remarks>
    /// Null check generation rules:
    /// <list type="bullet">
    ///   <item>Reference types (non-nullable): Generate null check</item>
    ///   <item>Reference types (nullable-annotated, e.g., string?): No null check</item>
    ///   <item>Value types (non-nullable): No null check (cannot be null)</item>
    ///   <item>Nullable value types (Nullable&lt;T&gt;, e.g., int?): Generate null check</item>
    /// </list>
    /// </remarks>
    private static bool ShouldGenerateNullCheck(ITypeSymbol type)
    {
        // Non-nullable reference types should have null checks
        if (type.IsReferenceType)
        {
            return type.NullableAnnotation != NullableAnnotation.Annotated;
        }

        // Nullable<T> value types (e.g., int?) should have null checks
        // Check if it's a Nullable<T> by looking for OriginalDefinition being System.Nullable<T>
        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return true;
        }

        // Regular value types cannot be null, no check needed
        return false;
    }

    /// <summary>
    /// Creates a preview result with the generated constructor code.
    /// When replacing a constructor that lives in another partial, also
    /// includes a Modify pending change per affected file with that
    /// constructor as <c>BeforeSnippet</c>.
    /// </summary>
    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        GenerateConstructorParams @params,
        List<ISymbol> members,
        ConstructorDeclarationSyntax constructor,
        IMethodSymbol? exactMatch,
        Solution solution,
        string? copyParameterName,
        bool addClassBaseCopy,
        CancellationToken cancellationToken)
    {
        var replacing = exactMatch != null;
        var memberNames = string.Join(", ", members.Select(m => m.Name));
        var inherited = @params.IncludeInheritedMembers ? " including inherited members" : "";
        var visibility = ResolveVisibility(@params.Visibility);
        var verb = replacing ? "Replace" : "Generate";
        var mode = @params.CopyConstructor
            ? $"copy constructor from '{copyParameterName}'"
            : "constructor";
        var classBaseCopyNote = !@params.ClassBaseCopy || !@params.CopyConstructor
            ? ""
            : addClassBaseCopy
                ? $" with class : base({copyParameterName}) initializer"
                : " (no class base-copy initializer was added)";

        // Show the generated constructor as the "after" snippet
        var afterSnippet = constructor.NormalizeWhitespace().ToFullString();

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = Contracts.Enums.ChangeKind.Modify,
                Description = $"{verb} {mode}{inherited} for {@params.TypeName} initializing: {memberNames} ({visibility}){classBaseCopyNote}",
                BeforeSnippet = replacing
                    ? $"// Type '{@params.TypeName}' (replacing existing constructor)"
                    : $"// Type '{@params.TypeName}' (no constructor with these parameters)",
                AfterSnippet = afterSnippet
            }
        };

        if (exactMatch != null)
        {
            var sourcePath = PathResolver.NormalizePath(@params.SourceFile);
            foreach (var reference in exactMatch.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (syntax is not ConstructorDeclarationSyntax existingCtor)
                    continue;

                var document = solution.GetDocument(syntax.SyntaxTree);
                var filePath = document?.FilePath ?? syntax.SyntaxTree.FilePath;
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;
                if (string.Equals(PathResolver.NormalizePath(filePath), sourcePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                pendingChanges.Add(new PendingChange
                {
                    File = filePath,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = $"Remove existing constructor from {@params.TypeName}",
                    BeforeSnippet = existingCtor.NormalizeWhitespace().ToFullString(),
                    AfterSnippet = "// constructor removed"
                });
            }
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

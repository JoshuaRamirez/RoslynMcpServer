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
/// (settable properties, not the readable-property equality collector).
/// </summary>
public sealed class GenerateConstructorOperation : RefactoringOperationBase<GenerateConstructorParams>
{
    /// <summary>
    /// Creates a new generate constructor operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public GenerateConstructorOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateConstructorParams @params)
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

        // Get fields and properties to initialize
        var members = GetMembersToInitialize(
            typeSymbol, @params.Members, @params.IncludeProperties, @params.IncludeInheritedMembers);

        if (members.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                "No members found to initialize in constructor.");
        }

        // Check for existing constructor with same signature or ambiguous due to optional params
        var parameterTypes = members.Select(m => GetMemberType(m)).ToList();
        var newParamCount = parameterTypes.Count;

        foreach (var ctor in typeSymbol.Constructors.Where(c => !c.IsImplicitlyDeclared))
        {
            // Exact signature match
            if (ctor.Parameters.Length == newParamCount &&
                ctor.Parameters.Select(p => p.Type).SequenceEqual(parameterTypes, SymbolEqualityComparer.Default))
            {
                throw new RefactoringException(
                    ErrorCodes.ConstructorExists,
                    "A constructor with the same signature already exists.");
            }

            // Check for ambiguity with optional parameters
            // Case 1: New constructor could be called where existing has optional params
            var requiredParams = ctor.Parameters.TakeWhile(p => !p.IsOptional).ToList();
            var requiredParamTypes = requiredParams.Select(p => p.Type).ToList();

            if (requiredParams.Count <= newParamCount && ctor.Parameters.Length >= newParamCount)
            {
                // Check if first N types match (where N is new param count)
                var ctorTypesSubset = ctor.Parameters.Take(newParamCount).Select(p => p.Type).ToList();
                if (ctorTypesSubset.SequenceEqual(parameterTypes, SymbolEqualityComparer.Default))
                {
                    throw new RefactoringException(
                        ErrorCodes.ConstructorExists,
                        $"New constructor would be ambiguous with existing constructor that has optional parameters.");
                }
            }

            // Case 2: Existing constructor with fewer params could match if new constructor adds optionals
            if (requiredParams.Count == newParamCount &&
                requiredParamTypes.SequenceEqual(parameterTypes, SymbolEqualityComparer.Default))
            {
                throw new RefactoringException(
                    ErrorCodes.ConstructorExists,
                    $"New constructor would conflict with existing constructor's required parameters.");
            }
        }

        // Generate the constructor
        var constructor = GenerateConstructor(members, typeDeclaration, @params.AddNullChecks);

        // If preview mode, return without applying (but include generated constructor code)
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, members, constructor);
        }

        // Add constructor to type
        var newTypeDeclaration = InsertConstructor(typeDeclaration, constructor);
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
        bool includeInheritedMembers)
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
        }

        return allMembers;
    }

    /// <summary>
    /// Collects instance fields and, when requested, settable properties declared
    /// on <paramref name="declaringType"/>. When <paramref name="requireAccessible"/>
    /// is true (inherited members), only members visible from
    /// <paramref name="fromType"/> are added, hidden/overridden names are skipped,
    /// and inherited readonly fields are omitted because a derived constructor
    /// cannot assign them.
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
                if (prop.IsStatic || prop.IsReadOnly || prop.SetMethod == null || prop.IsImplicitlyDeclared)
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

    private static ITypeSymbol GetMemberType(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new InvalidOperationException($"Unexpected member type: {member.GetType()}")
        };
    }

    private static ConstructorDeclarationSyntax GenerateConstructor(
        List<ISymbol> members,
        TypeDeclarationSyntax typeDeclaration,
        bool addNullChecks)
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
            {
                var nullCheck = SyntaxFactory.IfStatement(
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

                statements.Add(nullCheck);
            }

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
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block(statements))
            .NormalizeWhitespace();

        return constructor;
    }

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
    /// </summary>
    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        GenerateConstructorParams @params,
        List<ISymbol> members,
        ConstructorDeclarationSyntax constructor)
    {
        var memberNames = string.Join(", ", members.Select(m => m.Name));
        var inherited = @params.IncludeInheritedMembers ? " including inherited members" : "";

        // Show the generated constructor as the "after" snippet
        var afterSnippet = constructor.NormalizeWhitespace().ToFullString();

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = Contracts.Enums.ChangeKind.Modify,
                Description = $"Generate constructor{inherited} for {@params.TypeName} initializing: {memberNames}",
                BeforeSnippet = $"// Type '{@params.TypeName}' (no constructor with these parameters)",
                AfterSnippet = afterSnippet
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

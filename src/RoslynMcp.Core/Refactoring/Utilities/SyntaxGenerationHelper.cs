using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Helper methods for generating C# syntax nodes.
/// </summary>
public static class SyntaxGenerationHelper
{
    /// <summary>
    /// Creates a method stub for interface implementation or override.
    /// </summary>
    /// <param name="method">The method to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="callBase">If true, adds base.Method() call for overrides.</param>
    /// <param name="throwNotImplemented">If true, throws NotImplementedException.</param>
    /// <returns>Method declaration syntax.</returns>
    public static MethodDeclarationSyntax CreateMethodStub(
        IMethodSymbol method,
        bool explicitInterface = false,
        bool callBase = false,
        bool throwNotImplemented = true)
    {
        // Build parameter list, preserving each parameter's RefKind so a
        // regenerated override of M(ref/out/in/ref readonly T) still overrides.
        var parameters = method.Parameters.Select(CreateParameter);

        var parameterList = SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(parameters));

        // Build return type
        var returnType = SyntaxFactory.ParseTypeName(method.ReturnType.ToDisplayString());

        // Build body
        BlockSyntax body;
        if (callBase && !method.IsAbstract)
        {
            body = CreateBaseCallBody(method);
        }
        else if (throwNotImplemented)
        {
            body = CreateThrowNotImplementedBody();
        }
        else
        {
            body = CreateDefaultReturnBody(method.ReturnType);
        }

        // Build method declaration
        var methodDecl = SyntaxFactory.MethodDeclaration(returnType, method.Name)
            .WithParameterList(parameterList)
            .WithBody(body);

        if (explicitInterface && method.ContainingType != null)
        {
            // Explicit interface implementation: no modifiers, explicit name
            methodDecl = methodDecl.WithExplicitInterfaceSpecifier(
                SyntaxFactory.ExplicitInterfaceSpecifier(
                    SyntaxFactory.ParseName(method.ContainingType.ToDisplayString())));
        }
        else
        {
            // Implicit implementation or override. Overrides must keep the
            // inherited accessibility (CS0507); interface members are public
            // and must not use override (CS0115).
            var modifiers = new List<SyntaxToken>();
            modifiers.AddRange(AccessibilityModifierTokens(method.DeclaredAccessibility));

            if (NeedsOverrideModifier(method))
            {
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
            }

            methodDecl = methodDecl.WithModifiers(SyntaxFactory.TokenList(modifiers));
        }

        // Add type parameters if generic
        if (method.TypeParameters.Length > 0)
        {
            var typeParams = method.TypeParameters.Select(tp =>
                SyntaxFactory.TypeParameter(tp.Name));
            methodDecl = methodDecl.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)));
        }

        return methodDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Creates a property stub for interface implementation or override.
    /// </summary>
    /// <param name="property">The property to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="throwNotImplemented">If true, throws NotImplementedException in accessors.</param>
    /// <param name="callBase">If true, non-abstract accessors call <c>base.Prop</c>.</param>
    /// <returns>Property declaration syntax.</returns>
    public static PropertyDeclarationSyntax CreatePropertyStub(
        IPropertySymbol property,
        bool explicitInterface = false,
        bool throwNotImplemented = true,
        bool callBase = false)
    {
        var propertyType = SyntaxFactory.ParseTypeName(property.Type.ToDisplayString());
        var accessors = CreatePropertyAccessors(property, throwNotImplemented, callBase);

        var propDecl = SyntaxFactory.PropertyDeclaration(propertyType, property.Name)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));

        if (explicitInterface && property.ContainingType != null)
        {
            propDecl = propDecl.WithExplicitInterfaceSpecifier(
                SyntaxFactory.ExplicitInterfaceSpecifier(
                    SyntaxFactory.ParseName(property.ContainingType.ToDisplayString())));
        }
        else
        {
            // Overrides must keep the inherited accessibility (CS0507).
            // Interface members are public and must not use override (CS0115).
            var modifiers = new List<SyntaxToken>();
            modifiers.AddRange(AccessibilityModifierTokens(property.DeclaredAccessibility));

            if (NeedsOverrideModifier(property))
            {
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
            }

            propDecl = propDecl.WithModifiers(SyntaxFactory.TokenList(modifiers));
        }

        return propDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an indexer stub for interface implementation or override.
    /// </summary>
    /// <param name="indexer">The indexer to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="throwNotImplemented">If true, throws NotImplementedException in accessors.</param>
    /// <param name="callBase">If true, non-abstract accessors call <c>base[i]</c>.</param>
    /// <returns>Indexer declaration syntax.</returns>
    public static IndexerDeclarationSyntax CreateIndexerStub(
        IPropertySymbol indexer,
        bool explicitInterface = false,
        bool throwNotImplemented = true,
        bool callBase = false)
    {
        var indexerType = CreateMemberType(indexer.Type, indexer.ReturnsByRef, indexer.ReturnsByRefReadonly);
        var parameters = indexer.Parameters.Select(CreateParameter);
        var accessors = CreatePropertyAccessors(indexer, throwNotImplemented, callBase);

        var indexerDecl = SyntaxFactory.IndexerDeclaration(indexerType)
            .WithParameterList(SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));

        if (explicitInterface && indexer.ContainingType != null)
        {
            indexerDecl = indexerDecl.WithExplicitInterfaceSpecifier(
                SyntaxFactory.ExplicitInterfaceSpecifier(
                    SyntaxFactory.ParseName(indexer.ContainingType.ToDisplayString())));
        }
        else
        {
            var modifiers = new List<SyntaxToken>();
            modifiers.AddRange(AccessibilityModifierTokens(indexer.DeclaredAccessibility));

            if (NeedsOverrideModifier(indexer))
            {
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
            }

            indexerDecl = indexerDecl.WithModifiers(SyntaxFactory.TokenList(modifiers));
        }

        return indexerDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an event stub for interface implementation or override
    /// with empty add/remove accessors. Same body shape as
    /// <c>ImplementAbstractOperation.CreateEventStub</c>.
    /// </summary>
    /// <param name="eventSymbol">The event to implement or override.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="emittingType">
    /// Type that will declare the stub. When set and the event lives in
    /// another assembly, <c>protected internal</c> is emitted as
    /// <c>protected</c> (CS0507). Omitted / same-assembly is unchanged.
    /// </param>
    /// <returns>Event declaration syntax.</returns>
    public static EventDeclarationSyntax CreateEventStub(
        IEventSymbol eventSymbol,
        bool explicitInterface = false,
        INamedTypeSymbol? emittingType = null)
    {
        var eventType = SyntaxFactory.ParseTypeName(eventSymbol.Type.ToDisplayString());

        var addAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.AddAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());
        var removeAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.RemoveAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());

        var eventDecl = SyntaxFactory.EventDeclaration(eventType, eventSymbol.Name)
            .WithAccessorList(SyntaxFactory.AccessorList(
                SyntaxFactory.List(new[] { addAccessor, removeAccessor })));

        if (explicitInterface && eventSymbol.ContainingType != null)
        {
            eventDecl = eventDecl.WithExplicitInterfaceSpecifier(
                SyntaxFactory.ExplicitInterfaceSpecifier(
                    SyntaxFactory.ParseName(eventSymbol.ContainingType.ToDisplayString())));
        }
        else
        {
            // Overrides must keep the inherited accessibility (CS0507).
            // Interface members are public and must not use override (CS0115).
            // Across assemblies, protected internal must be protected.
            var modifiers = new List<SyntaxToken>();
            modifiers.AddRange(AccessibilityModifierTokens(
                OverrideAccessibility(eventSymbol, emittingType)));

            if (NeedsOverrideModifier(eventSymbol))
            {
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
            }

            eventDecl = eventDecl.WithModifiers(SyntaxFactory.TokenList(modifiers));
        }

        return eventDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Converts a return type to its async equivalent.
    /// </summary>
    /// <param name="returnType">The original return type.</param>
    /// <returns>The async return type (Task or Task&lt;T&gt;).</returns>
    public static TypeSyntax ToAsyncReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return SyntaxFactory.ParseTypeName("Task");
        }

        return SyntaxFactory.GenericName("Task")
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                    SyntaxFactory.ParseTypeName(returnType.ToDisplayString()))));
    }

    /// <summary>
    /// Creates a field declaration from a property.
    /// </summary>
    /// <param name="property">The property to back with a field.</param>
    /// <returns>Field declaration syntax.</returns>
    public static FieldDeclarationSyntax CreateBackingField(IPropertySymbol property)
    {
        var fieldName = "_" + char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
        var fieldType = SyntaxFactory.ParseTypeName(property.Type.ToDisplayString());

        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(fieldType)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(fieldName))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates a property from a field (encapsulation).
    /// </summary>
    /// <param name="field">The field to encapsulate.</param>
    /// <param name="propertyName">Name for the property.</param>
    /// <param name="readOnly">If true, creates read-only property.</param>
    /// <returns>Property declaration syntax.</returns>
    public static PropertyDeclarationSyntax CreatePropertyFromField(
        IFieldSymbol field,
        string propertyName,
        bool readOnly = false)
    {
        var propertyType = SyntaxFactory.ParseTypeName(field.Type.ToDisplayString());

        var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(field.Name))));

        var accessors = new List<AccessorDeclarationSyntax> { getAccessor };

        if (!readOnly && !field.IsReadOnly)
        {
            var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(field.Name),
                            SyntaxFactory.IdentifierName("value")))));
            accessors.Add(setAccessor);
        }

        return SyntaxFactory.PropertyDeclaration(propertyType, propertyName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an interface declaration with extracted members.
    /// </summary>
    /// <param name="interfaceName">Name of the interface.</param>
    /// <param name="members">Members to include in interface.</param>
    /// <returns>Interface declaration syntax.</returns>
    public static InterfaceDeclarationSyntax CreateInterfaceDeclaration(
        string interfaceName,
        IEnumerable<ISymbol> members)
    {
        var memberSyntax = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? syntax = member switch
            {
                IMethodSymbol method => CreateInterfaceMethod(method),
                IPropertySymbol { IsIndexer: true } indexer => CreateInterfaceIndexer(indexer),
                IPropertySymbol prop => CreateInterfaceProperty(prop),
                IEventSymbol evt => CreateInterfaceEvent(evt),
                _ => null
            };

            if (syntax != null)
            {
                memberSyntax.Add(syntax);
            }
        }

        return SyntaxFactory.InterfaceDeclaration(interfaceName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List(memberSyntax))
            .NormalizeWhitespace();
    }

    private static MethodDeclarationSyntax CreateInterfaceMethod(IMethodSymbol method)
    {
        var parameters = method.Parameters.Select(p =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                .WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString())
                    .WithTrailingTrivia(SyntaxFactory.Space)));

        var returnType = SyntaxFactory.ParseTypeName(method.ReturnType.ToDisplayString());

        var methodDecl = SyntaxFactory.MethodDeclaration(returnType, method.Name)
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        if (method.TypeParameters.Length > 0)
        {
            var typeParams = method.TypeParameters.Select(tp =>
                SyntaxFactory.TypeParameter(tp.Name));
            methodDecl = methodDecl.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)));
        }

        return methodDecl;
    }

    private static PropertyDeclarationSyntax CreateInterfaceProperty(IPropertySymbol property)
    {
        var accessors = new List<AccessorDeclarationSyntax>();

        if (property.GetMethod != null)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (property.SetMethod != null)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return SyntaxFactory.PropertyDeclaration(
                SyntaxFactory.ParseTypeName(property.Type.ToDisplayString()),
                property.Name)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));
    }

    /// <summary>
    /// Interface indexer: <c>this[params] { get; set; }</c> with the real
    /// parameter list / <c>RefKind</c> and <c>ref</c> / <c>ref readonly</c>
    /// return kind. Only publicly accessible accessors are emitted so a
    /// private setter cannot become a public interface <c>set;</c>. An
    /// init-only setter is emitted as <c>init;</c>. Ordinary properties
    /// stay on <see cref="CreateInterfaceProperty"/>.
    /// </summary>
    private static IndexerDeclarationSyntax? CreateInterfaceIndexer(IPropertySymbol indexer)
    {
        var accessors = new List<AccessorDeclarationSyntax>();

        if (IsPublicInterfaceAccessor(indexer.GetMethod))
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (IsPublicInterfaceAccessor(indexer.SetMethod))
        {
            var kind = indexer.SetMethod!.IsInitOnly
                ? SyntaxKind.InitAccessorDeclaration
                : SyntaxKind.SetAccessorDeclaration;
            accessors.Add(SyntaxFactory.AccessorDeclaration(kind)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (accessors.Count == 0)
            return null;

        var parameters = indexer.Parameters.Select(CreateParameter);
        return SyntaxFactory.IndexerDeclaration(
                CreateMemberType(indexer.Type, indexer.ReturnsByRef, indexer.ReturnsByRefReadonly))
            .WithParameterList(SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));
    }

    private static bool IsPublicInterfaceAccessor(IMethodSymbol? accessor) =>
        accessor is { DeclaredAccessibility: Accessibility.Public };

    private static EventDeclarationSyntax CreateInterfaceEvent(IEventSymbol eventSymbol)
    {
        // Interface events use field syntax
        return SyntaxFactory.EventDeclaration(
                SyntaxFactory.ParseTypeName(eventSymbol.Type.ToDisplayString()),
                eventSymbol.Name)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>
    /// Interface members are abstract but must be implemented without
    /// <c>override</c> (CS0115). Abstract-class / virtual / override
    /// members still need <c>override</c>.
    /// </summary>
    private static bool NeedsOverrideModifier(ISymbol member) =>
        member.ContainingType?.TypeKind != TypeKind.Interface
        && (member.IsAbstract || member.IsVirtual || member.IsOverride);

    /// <summary>
    /// Same-assembly: keep <see cref="ISymbol.DeclaredAccessibility"/>.
    /// Cross-assembly <c>protected internal</c> becomes <c>protected</c>
    /// (CS0507). Other accessibilities are unchanged.
    /// </summary>
    internal static Accessibility OverrideAccessibility(ISymbol member, INamedTypeSymbol? emittingType)
    {
        var accessibility = member.DeclaredAccessibility;
        if (emittingType == null)
            return accessibility;
        if (accessibility != Accessibility.ProtectedOrInternal)
            return accessibility;
        if (SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, emittingType.ContainingAssembly))
            return accessibility;
        return Accessibility.Protected;
    }

    /// <summary>
    /// True when a getter must throw: the flag is on, or the indexer /
    /// property returns <c>ref</c> / <c>ref readonly</c> (CS8156).
    /// </summary>
    private static bool RequiresThrowBody(IPropertySymbol property, bool throwNotImplemented) =>
        throwNotImplemented || property.ReturnsByRef || property.ReturnsByRefReadonly;

    private static TypeSyntax CreateMemberType(ITypeSymbol type, bool returnsByRef, bool returnsByRefReadonly)
    {
        var inner = SyntaxFactory.ParseTypeName(type.ToDisplayString());
        if (returnsByRefReadonly)
        {
            return SyntaxFactory.RefType(
                SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                inner);
        }

        if (returnsByRef)
        {
            return SyntaxFactory.RefType(
                SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                inner);
        }

        return inner;
    }

    private static List<AccessorDeclarationSyntax> CreatePropertyAccessors(
        IPropertySymbol property,
        bool throwNotImplemented,
        bool callBase)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        var useBase = callBase && !property.IsAbstract;

        if (property.GetMethod != null)
        {
            var getBody = useBase
                ? CreateBasePropertyGetBody(property)
                : RequiresThrowBody(property, throwNotImplemented)
                    ? CreateThrowNotImplementedBody()
                    : CreateDefaultReturnBody(property.Type);
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithBody(getBody));
        }

        if (property.SetMethod != null)
        {
            var setBody = useBase
                ? CreateBasePropertySetBody(property)
                : throwNotImplemented
                    ? CreateThrowNotImplementedBody()
                    : SyntaxFactory.Block();
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithBody(setBody));
        }

        return accessors;
    }

    private static ExpressionSyntax CreateBasePropertyAccess(IPropertySymbol property)
    {
        if (property.IsIndexer)
        {
            var arguments = property.Parameters.Select(CreateBaseCallArgument);
            return SyntaxFactory.ElementAccessExpression(
                SyntaxFactory.BaseExpression(),
                SyntaxFactory.BracketedArgumentList(SyntaxFactory.SeparatedList(arguments)));
        }

        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.BaseExpression(),
            SyntaxFactory.IdentifierName(property.Name));
    }

    private static BlockSyntax CreateBasePropertyGetBody(IPropertySymbol property) =>
        SyntaxFactory.Block(SyntaxFactory.ReturnStatement(CreateBasePropertyAccess(property)));

    private static BlockSyntax CreateBasePropertySetBody(IPropertySymbol property) =>
        SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    CreateBasePropertyAccess(property),
                    SyntaxFactory.IdentifierName("value"))));

    private static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.IdentifierName("NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }

    private static ParameterSyntax CreateParameter(IParameterSymbol parameter)
    {
        var syntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name))
            .WithType(SyntaxFactory.ParseTypeName(parameter.Type.ToDisplayString())
                .WithTrailingTrivia(SyntaxFactory.Space));

        var modifiers = RefKindParameterModifiers(parameter.RefKind);
        return modifiers.Count == 0 ? syntax : syntax.WithModifiers(modifiers);
    }

    private static SyntaxTokenList RefKindParameterModifiers(RefKind refKind) =>
        refKind switch
        {
            RefKind.Ref => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            RefKind.Out => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.OutKeyword)),
            RefKind.In => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword)),
            RefKind.RefReadOnlyParameter => SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)),
            _ => default
        };

    private static IEnumerable<SyntaxToken> AccessibilityModifierTokens(Accessibility accessibility) =>
        accessibility switch
        {
            Accessibility.Private => new[] { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) },
            Accessibility.Protected => new[] { SyntaxFactory.Token(SyntaxKind.ProtectedKeyword) },
            Accessibility.Internal => new[] { SyntaxFactory.Token(SyntaxKind.InternalKeyword) },
            Accessibility.ProtectedOrInternal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
                SyntaxFactory.Token(SyntaxKind.InternalKeyword)
            },
            Accessibility.ProtectedAndInternal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)
            },
            _ => new[] { SyntaxFactory.Token(SyntaxKind.PublicKeyword) }
        };

    private static BlockSyntax CreateBaseCallBody(IMethodSymbol method)
    {
        var arguments = method.Parameters.Select(CreateBaseCallArgument);

        var baseCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.BaseExpression(),
                SyntaxFactory.IdentifierName(method.Name)))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

        if (method.ReturnsVoid)
        {
            return SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(baseCall));
        }

        return SyntaxFactory.Block(SyntaxFactory.ReturnStatement(baseCall));
    }

    private static ArgumentSyntax CreateBaseCallArgument(IParameterSymbol parameter)
    {
        var argument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Name));
        var keyword = RefKindArgumentKeyword(parameter.RefKind);
        return keyword.HasValue
            ? argument.WithRefKindKeyword(keyword.Value)
            : argument;
    }

    /// <summary>
    /// Call-site ref-kind keyword. <c>ref readonly</c> parameters are passed
    /// with <c>in</c> (the valid argument modifier).
    /// </summary>
    private static SyntaxToken? RefKindArgumentKeyword(RefKind refKind) =>
        refKind switch
        {
            RefKind.Ref => SyntaxFactory.Token(SyntaxKind.RefKeyword),
            RefKind.Out => SyntaxFactory.Token(SyntaxKind.OutKeyword),
            RefKind.In => SyntaxFactory.Token(SyntaxKind.InKeyword),
            RefKind.RefReadOnlyParameter => SyntaxFactory.Token(SyntaxKind.InKeyword),
            _ => null
        };

    /// <summary>
    /// Creates a default-return method / getter body: an empty block for
    /// <c>void</c>, <c>return null;</c> for reference types, and
    /// <c>return default(T);</c> for value types and type parameters.
    /// </summary>
    /// <param name="returnType">The method or accessor return type.</param>
    /// <returns>A block containing the default-return (or empty for void).</returns>
    public static BlockSyntax CreateDefaultReturnBody(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return SyntaxFactory.Block();
        }

        ExpressionSyntax defaultExpr;
        if (returnType.IsReferenceType)
        {
            defaultExpr = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        }
        else
        {
            defaultExpr = SyntaxFactory.DefaultExpression(
                SyntaxFactory.ParseTypeName(returnType.ToDisplayString()));
        }

        return SyntaxFactory.Block(SyntaxFactory.ReturnStatement(defaultExpr));
    }
}

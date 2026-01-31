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
        // Build parameter list
        var parameters = method.Parameters.Select(p =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                .WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString())
                    .WithTrailingTrivia(SyntaxFactory.Space)));

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
            // Implicit implementation or override
            var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };

            if (method.IsAbstract || method.IsVirtual || method.IsOverride)
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
    /// <returns>Property declaration syntax.</returns>
    public static PropertyDeclarationSyntax CreatePropertyStub(
        IPropertySymbol property,
        bool explicitInterface = false,
        bool throwNotImplemented = true)
    {
        var propertyType = SyntaxFactory.ParseTypeName(property.Type.ToDisplayString());
        var accessors = new List<AccessorDeclarationSyntax>();

        if (property.GetMethod != null)
        {
            var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration);
            if (throwNotImplemented)
            {
                getAccessor = getAccessor.WithBody(CreateThrowNotImplementedBody());
            }
            else
            {
                getAccessor = getAccessor.WithBody(CreateDefaultReturnBody(property.Type));
            }
            accessors.Add(getAccessor);
        }

        if (property.SetMethod != null)
        {
            var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration);
            if (throwNotImplemented)
            {
                setAccessor = setAccessor.WithBody(CreateThrowNotImplementedBody());
            }
            else
            {
                setAccessor = setAccessor.WithBody(SyntaxFactory.Block());
            }
            accessors.Add(setAccessor);
        }

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
            var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };

            if (property.IsAbstract || property.IsVirtual || property.IsOverride)
            {
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
            }

            propDecl = propDecl.WithModifiers(SyntaxFactory.TokenList(modifiers));
        }

        return propDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an event stub for interface implementation.
    /// </summary>
    /// <param name="eventSymbol">The event to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <returns>Event declaration syntax.</returns>
    public static EventDeclarationSyntax CreateEventStub(
        IEventSymbol eventSymbol,
        bool explicitInterface = false)
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
            eventDecl = eventDecl.WithModifiers(
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
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

    private static EventDeclarationSyntax CreateInterfaceEvent(IEventSymbol eventSymbol)
    {
        // Interface events use field syntax
        return SyntaxFactory.EventDeclaration(
                SyntaxFactory.ParseTypeName(eventSymbol.Type.ToDisplayString()),
                eventSymbol.Name)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.IdentifierName("NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }

    private static BlockSyntax CreateBaseCallBody(IMethodSymbol method)
    {
        var arguments = method.Parameters.Select(p =>
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Name)));

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

    private static BlockSyntax CreateDefaultReturnBody(ITypeSymbol returnType)
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

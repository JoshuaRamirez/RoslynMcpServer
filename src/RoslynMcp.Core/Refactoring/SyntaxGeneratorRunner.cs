using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Transactions;
using RoslynMcp.Core.Coordinates;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Layer 4 declaration mutation engine backed by Roslyn <see cref="SyntaxGenerator"/> and <see cref="SymbolEditor"/>.
/// </summary>
public static class SyntaxGeneratorRunner
{
    /// <summary>
    /// Executes a <see cref="SyntaxGeneratorOperation"/> against <paramref name="solution"/> and returns the updated <see cref="Solution"/>.
    /// </summary>
    /// <param name="solution">Input solution snapshot.</param>
    /// <param name="operation">Declaration mutation operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated solution snapshot.</returns>
    public static async Task<Solution> ExecuteAsync(
        Solution solution,
        SyntaxGeneratorOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(operation.Declaration);

        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, operation.Target, cancellationToken).ConfigureAwait(false);
        var document = target.Document;
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain syntax root.");
        var generator = SyntaxGenerator.GetGenerator(document);
        var payload = operation.Declaration;

        switch (payload.Action)
        {
            case SyntaxGeneratorActionKind.AddField:
            {
                var typeDecl = FindTargetTypeDeclaration(target.Node);
                var fieldType = ParseTypeSyntax(generator, payload.TypeName ?? "object");
                var accessibility = ParseAccessibility(payload.Accessibility, Accessibility.Private);
                var modifiers = ParseModifiers(payload.Modifiers);
                var initializer = !string.IsNullOrWhiteSpace(payload.InitializerExpression)
                    ? SyntaxFactory.ParseExpression(payload.InitializerExpression!)
                    : null;

                var fieldDecl = generator.FieldDeclaration(
                    payload.Name,
                    fieldType,
                    accessibility,
                    modifiers,
                    initializer);

                var updatedType = generator.AddMembers(typeDecl, fieldDecl);
                var newRoot = root.ReplaceNode(typeDecl, updatedType).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            case SyntaxGeneratorActionKind.AddProperty:
            {
                var typeDecl = FindTargetTypeDeclaration(target.Node);
                var propType = ParseTypeSyntax(generator, payload.TypeName ?? "object");
                var accessibility = ParseAccessibility(payload.Accessibility, Accessibility.Public);
                var modifiers = ParseModifiers(payload.Modifiers);

                SyntaxNode propDecl;
                if (payload.Statements is { Count: > 0 })
                {
                    var getAccessors = payload.Statements.Select(s => SyntaxFactory.ParseStatement(s)).ToArray();
                    propDecl = generator.PropertyDeclaration(
                        payload.Name,
                        propType,
                        accessibility,
                        modifiers,
                        getAccessorStatements: getAccessors);
                }
                else
                {
                    var accessModifier = AccessibilityToText(accessibility);
                    var modText = ModifiersToText(modifiers);
                    var initSuffix = !string.IsNullOrWhiteSpace(payload.InitializerExpression)
                        ? $" = {payload.InitializerExpression};"
                        : string.Empty;
                    propDecl = SyntaxFactory.ParseMemberDeclaration(
                        $"{accessModifier}{modText}{payload.TypeName ?? "object"} {payload.Name} {{ get; set; }}{initSuffix}")!;
                }

                var updatedType = generator.AddMembers(typeDecl, propDecl);
                var newRoot = root.ReplaceNode(typeDecl, updatedType).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            case SyntaxGeneratorActionKind.AddMethod:
            {
                var typeDecl = FindTargetTypeDeclaration(target.Node);
                var accessibility = ParseAccessibility(payload.Accessibility, Accessibility.Public);
                var modifiers = ParseModifiers(payload.Modifiers);
                var parameters = (payload.Parameters ?? [])
                    .Select(p => CreateParameterDeclaration(generator, p))
                    .ToArray();

                var statements = (payload.Statements ?? [])
                    .Select(s => (SyntaxNode)SyntaxFactory.ParseStatement(s.EndsWith(';') || s.EndsWith('}') ? s : s + ";"))
                    .ToArray();

                var returnType = string.IsNullOrWhiteSpace(payload.TypeName) ||
                                 string.Equals(payload.TypeName, "void", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ParseTypeSyntax(generator, payload.TypeName!);

                var methodDecl = generator.MethodDeclaration(
                    payload.Name,
                    parameters,
                    typeParameters: null,
                    returnType: returnType,
                    accessibility: accessibility,
                    modifiers: modifiers,
                    statements: statements);

                var updatedType = generator.AddMembers(typeDecl, methodDecl);
                var newRoot = root.ReplaceNode(typeDecl, updatedType).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            case SyntaxGeneratorActionKind.AddParameter:
            {
                var memberNode = target.Node.AncestorsAndSelf()
                    .FirstOrDefault(n => n is MethodDeclarationSyntax or ConstructorDeclarationSyntax)
                    ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "AddParameter target must resolve to a method or constructor.");

                var paramSpec = payload.Parameters?.FirstOrDefault()
                    ?? new ParameterSpec(payload.Name, payload.TypeName ?? "object", payload.InitializerExpression);
                var paramDecl = CreateParameterDeclaration(generator, paramSpec);

                var updatedMember = generator.AddParameters(memberNode, [paramDecl]);
                var newRoot = root.ReplaceNode(memberNode, updatedMember).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            case SyntaxGeneratorActionKind.AddAttribute:
            {
                var declNode = target.Node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault()
                    ?? target.Node;
                var attrName = !string.IsNullOrWhiteSpace(payload.AttributeName) ? payload.AttributeName! : payload.Name;
                var attrArgs = (payload.AttributeArguments ?? [])
                    .Select(a => generator.AttributeArgument(SyntaxFactory.ParseExpression(a)))
                    .ToArray();

                var attrNode = generator.Attribute(attrName, attrArgs);
                var updatedDecl = generator.AddAttributes(declNode, attrNode);
                var newRoot = root.ReplaceNode(declNode, updatedDecl).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            case SyntaxGeneratorActionKind.SetModifiers:
            {
                var declNode = target.Node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault()
                    ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "SetModifiers target must resolve to a member or type declaration.");

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var declaredSymbol = semanticModel?.GetDeclaredSymbol(declNode, cancellationToken);

                // Use SymbolEditor when modifying a multi-declaration symbol across partial definitions
                if (declaredSymbol is not null && declaredSymbol.DeclaringSyntaxReferences.Length > 1)
                {
                    var symbolEditor = SymbolEditor.Create(solution);
                    await symbolEditor.EditAllDeclarationsAsync(
                        declaredSymbol,
                        (docEditor, syntaxNode) =>
                        {
                            var updated = syntaxNode;
                            if (!string.IsNullOrWhiteSpace(payload.Accessibility))
                            {
                                updated = docEditor.Generator.WithAccessibility(updated, ParseAccessibility(payload.Accessibility, Accessibility.NotApplicable));
                            }

                            if (payload.Modifiers is not null)
                            {
                                updated = docEditor.Generator.WithModifiers(updated, ParseModifiers(payload.Modifiers));
                            }

                            docEditor.ReplaceNode(syntaxNode, updated);
                        },
                        cancellationToken).ConfigureAwait(false);

                    return symbolEditor.ChangedSolution;
                }

                var modifiedNode = (SyntaxNode)declNode;
                if (!string.IsNullOrWhiteSpace(payload.Accessibility))
                {
                    modifiedNode = generator.WithAccessibility(modifiedNode, ParseAccessibility(payload.Accessibility, Accessibility.Public));
                }

                if (payload.Modifiers is not null)
                {
                    modifiedNode = generator.WithModifiers(modifiedNode, ParseModifiers(payload.Modifiers));
                }

                var newRoot = root.ReplaceNode(declNode, modifiedNode).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            case SyntaxGeneratorActionKind.AddInterfaceImplementation:
            {
                var typeDecl = FindTargetTypeDeclaration(target.Node);
                var interfaceTypeSyntax = ParseTypeSyntax(generator, payload.Name);
                var updatedType = (TypeDeclarationSyntax)generator.AddInterfaceType(typeDecl, interfaceTypeSyntax);

                // Discover interface symbol in compilation and synthesize missing member stubs
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (semanticModel is not null)
                {
                    var ifaceSymbol = FindInterfaceSymbol(semanticModel.Compilation, payload.Name);
                    var existingNames = typeDecl.Members
                        .Select(m => generator.GetName(m))
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToHashSet(StringComparer.Ordinal);

                    if (ifaceSymbol is not null)
                    {
                        var stubMembers = new List<SyntaxNode>();
                        foreach (var member in ifaceSymbol.GetMembers())
                        {
                            if (existingNames.Contains(member.Name))
                            {
                                continue;
                            }

                            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                            {
                                var parameters = method.Parameters
                                    .Select(p => generator.ParameterDeclaration(p.Name, generator.TypeExpression(p.Type)))
                                    .ToArray();
                                var returnType = method.ReturnsVoid ? null : generator.TypeExpression(method.ReturnType);
                                var bodyStmt = method.ReturnsVoid
                                    ? []
                                    : new SyntaxNode[] { generator.ReturnStatement(generator.DefaultExpression(method.ReturnType)) };
                                stubMembers.Add(generator.MethodDeclaration(
                                    method.Name,
                                    parameters,
                                    returnType: returnType,
                                    accessibility: Accessibility.Public,
                                    statements: bodyStmt));
                            }
                            else if (member is IPropertySymbol prop)
                            {
                                var propTypeText = prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                                var accessors = prop.IsReadOnly ? "{ get; }" : "{ get; set; }";
                                stubMembers.Add(SyntaxFactory.ParseMemberDeclaration($"public {propTypeText} {prop.Name} {accessors}")!);
                            }
                        }

                        if (stubMembers.Count > 0)
                        {
                            updatedType = (TypeDeclarationSyntax)generator.AddMembers(updatedType, stubMembers);
                        }
                    }
                }

                var newRoot = root.ReplaceNode(typeDecl, updatedType).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            case SyntaxGeneratorActionKind.ReplaceMember:
            {
                var memberNode = target.Node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault()
                    ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "ReplaceMember target must resolve to a member declaration.");
                var replacementSource = payload.Statements is { Count: > 0 }
                    ? string.Join(Environment.NewLine, payload.Statements)
                    : payload.InitializerExpression ?? throw new RefactoringException(ErrorCodes.MissingRequiredParam, "ReplaceMember requires Statements or InitializerExpression.");

                var replacementMember = SyntaxFactory.ParseMemberDeclaration(replacementSource)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse replacement member declaration.");

                var newRoot = root.ReplaceNode(memberNode, replacementMember).NormalizeWhitespace();
                return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), payload.Action, "Unsupported SyntaxGeneratorActionKind.");
        }
    }

    private static TypeDeclarationSyntax FindTargetTypeDeclaration(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault()
        ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "Target coordinate did not resolve within a TypeDeclarationSyntax.");

    private static SyntaxNode ParseTypeSyntax(SyntaxGenerator generator, string typeName) =>
        typeName.Trim() switch
        {
            "bool" => generator.TypeExpression(SpecialType.System_Boolean),
            "byte" => generator.TypeExpression(SpecialType.System_Byte),
            "char" => generator.TypeExpression(SpecialType.System_Char),
            "decimal" => generator.TypeExpression(SpecialType.System_Decimal),
            "double" => generator.TypeExpression(SpecialType.System_Double),
            "float" => generator.TypeExpression(SpecialType.System_Single),
            "int" => generator.TypeExpression(SpecialType.System_Int32),
            "long" => generator.TypeExpression(SpecialType.System_Int64),
            "object" => generator.TypeExpression(SpecialType.System_Object),
            "short" => generator.TypeExpression(SpecialType.System_Int16),
            "string" => generator.TypeExpression(SpecialType.System_String),
            _ => SyntaxFactory.ParseTypeName(typeName.Trim())
        };

    private static SyntaxNode CreateParameterDeclaration(SyntaxGenerator generator, ParameterSpec spec)
    {
        var typeNode = ParseTypeSyntax(generator, spec.TypeName);
        var initializer = !string.IsNullOrWhiteSpace(spec.DefaultValue)
            ? SyntaxFactory.ParseExpression(spec.DefaultValue!)
            : null;
        return generator.ParameterDeclaration(spec.Name, typeNode, initializer);
    }

    private static Accessibility ParseAccessibility(string? raw, Accessibility defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "public" => Accessibility.Public,
            "internal" => Accessibility.Internal,
            "protected" => Accessibility.Protected,
            "private" => Accessibility.Private,
            "protectedinternal" or "protected internal" or "protectedorinternal" => Accessibility.ProtectedOrInternal,
            "privateprotected" or "private protected" or "protectedandinternal" => Accessibility.ProtectedAndInternal,
            _ => defaultValue
        };
    }

    private static DeclarationModifiers ParseModifiers(IReadOnlyList<string>? modifiers)
    {
        if (modifiers is null || modifiers.Count == 0)
        {
            return DeclarationModifiers.None;
        }

        var result = DeclarationModifiers.None;
        foreach (var mod in modifiers)
        {
            result |= mod.Trim().ToLowerInvariant() switch
            {
                "static" => DeclarationModifiers.Static,
                "readonly" => DeclarationModifiers.ReadOnly,
                "virtual" => DeclarationModifiers.Virtual,
                "override" => DeclarationModifiers.Override,
                "abstract" => DeclarationModifiers.Abstract,
                "sealed" => DeclarationModifiers.Sealed,
                "async" => DeclarationModifiers.Async,
                "const" => DeclarationModifiers.Const,
                "partial" => DeclarationModifiers.Partial,
                _ => DeclarationModifiers.None
            };
        }

        return result;
    }

    private static string AccessibilityToText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public ",
        Accessibility.Internal => "internal ",
        Accessibility.Protected => "protected ",
        Accessibility.Private => "private ",
        Accessibility.ProtectedOrInternal => "protected internal ",
        Accessibility.ProtectedAndInternal => "private protected ",
        _ => "public "
    };

    private static string ModifiersToText(DeclarationModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.IsStatic) parts.Add("static ");
        if (modifiers.IsReadOnly) parts.Add("readonly ");
        if (modifiers.IsVirtual) parts.Add("virtual ");
        if (modifiers.IsOverride) parts.Add("override ");
        if (modifiers.IsAbstract) parts.Add("abstract ");
        if (modifiers.IsSealed) parts.Add("sealed ");
        return string.Concat(parts);
    }

    private static INamedTypeSymbol? FindInterfaceSymbol(Compilation compilation, string interfaceName)
    {
        var clean = interfaceName.Trim();
        var byMetadata = compilation.GetTypeByMetadataName(clean);
        if (byMetadata is { TypeKind: TypeKind.Interface })
        {
            return byMetadata;
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var ifaceNode in tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            {
                if (string.Equals(ifaceNode.Identifier.Text, clean, StringComparison.Ordinal) ||
                    clean.EndsWith("." + ifaceNode.Identifier.Text, StringComparison.Ordinal))
                {
                    if (model.GetDeclaredSymbol(ifaceNode) is { } sym)
                    {
                        return sym;
                    }
                }
            }
        }

        return null;
    }
}

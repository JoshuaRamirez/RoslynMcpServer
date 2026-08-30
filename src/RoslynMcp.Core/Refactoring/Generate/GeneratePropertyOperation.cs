using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates a property on a selected type: auto-property <c>{ get; set; }</c>,
/// init-only <c>{ get; init; }</c>, or a backing-field form when a field is the target.
/// Honors optional <c>line</c> / <c>column</c> to disambiguate same-named
/// types in one file (identifier preferred, then smallest containing type).
/// Selection uses <c>BaseTypeDeclarationSyntax</c> so an earlier same-named
/// enum/delegate still reaches <c>InvalidSymbolKind</c>.
/// Honors <c>replaceExisting</c> to remove an existing property of the same
/// name (including across partials) before inserting a freshly generated one.
/// </summary>
public sealed class GeneratePropertyOperation : RefactoringOperationBase<GeneratePropertyParams>
{
    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "private", "protected", "internal", "protected internal", "private protected"
    };

    /// <summary>
    /// Creates a new generate property operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public GeneratePropertyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GeneratePropertyParams @params) => Validate(@params);

    /// <summary>
    /// Validates generate-property parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(GeneratePropertyParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        var hasField = !string.IsNullOrWhiteSpace(@params.FieldName);
        if (string.IsNullOrWhiteSpace(@params.PropertyName) && !hasField)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "propertyName is required unless fieldName is provided.");

        if (string.IsNullOrWhiteSpace(@params.PropertyType) && !hasField)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "propertyType is required unless fieldName is provided.");

        if (@params.PropertyName != null && !IsValidIdentifier(@params.PropertyName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid property name: {@params.PropertyName}");

        if (!string.IsNullOrWhiteSpace(@params.Visibility) && !ValidVisibilities.Contains(@params.Visibility.Trim()))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"Invalid visibility: {@params.Visibility}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <summary>
    /// Rejects documents that cannot receive source edits.
    /// </summary>
    internal static void ValidateDocumentIsEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (source-generated).");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable.");
        }

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        GeneratePropertyParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Optional line/column disambiguates same-named types. Omitted
        // column keeps today's BaseTypeDeclarationSyntax FirstOrDefault
        // pick (including enum/delegate). Line/column set includes those
        // unsupported candidates so a covering enum still reaches
        // InvalidSymbolKind instead of retargeting a later class.
        var typeDecl = FindTypeDeclaration(
            root, @params.TypeName, @params.Line, @params.Column, out var hadCandidates);

        if (typeDecl == null)
        {
            // Empty typeName set is today's SymbolNotFound, even when
            // column+line is supplied. A positional miss (name exists,
            // nothing covers that column+line) is TypeNotFound — do not
            // silently first-match.
            if (hadCandidates && @params.Column.HasValue && @params.Line.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"Type '{@params.TypeName}' not found in file.");
            }

            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No type named '{@params.TypeName}' found in the source file.");
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"Could not resolve a symbol for type '{@params.TypeName}'.");
        }

        ValidateTypeCanHostProperty(typeSymbol);

        if (typeDecl is not TypeDeclarationSyntax hostTypeDecl)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for generate_property.");
        }

        IFieldSymbol? backingField = null;
        if (!string.IsNullOrWhiteSpace(@params.FieldName))
        {
            backingField = ResolveBackingField(typeSymbol, @params.FieldName);
            ValidateFieldCanBeWrapped(backingField, @params.InitOnly);
        }

        var propertyName = ResolvePropertyName(@params, backingField);
        var propertyType = ResolvePropertyType(@params, backingField);
        var existingProperty = ResolvePropertyToReplace(typeSymbol, propertyName, @params.ReplaceExisting);
        ValidateAutoPropertyAccessors(typeSymbol, @params.InitOnly, hasBackingField: backingField != null);

        var visibility = string.IsNullOrWhiteSpace(@params.Visibility) ? "public" : @params.Visibility.Trim();
        var property = backingField != null
            ? CreatePropertyWithBackingField(propertyName, propertyType, backingField, visibility, @params.InitOnly)
            : CreateAutoProperty(propertyName, propertyType, visibility, @params.InitOnly);

        if (ShouldBeStatic(typeSymbol, backingField))
            property = AddStaticModifier(property);

        var replacing = existingProperty != null;
        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                propertyName,
                property,
                existingProperty,
                document.Project.Solution,
                cancellationToken);
        }

        var solution = document.Project.Solution;
        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later replaceExisting on another type
        // would recover the stale node via FirstOrDefault.
        SyntaxAnnotation? targetTypeAnnotation = null;
        if (replacing)
        {
            // Annotate before the rewrite. Removing a property from an
            // earlier same-file partial shifts both SpanStart and the
            // physical line of a later selected partial — do not re-find
            // with those stale values. Today's FindTypeDeclaration(root,
            // typeName, preferredSpanStart) is not enough.
            targetTypeAnnotation = new SyntaxAnnotation("generate-property-target-type");
            root = root.ReplaceNode(
                hostTypeDecl,
                hostTypeDecl.WithAdditionalAnnotations(targetTypeAnnotation));
            document = document.WithSyntaxRoot(root);
            solution = document.Project.Solution;

            solution = await RemoveExistingPropertiesAcrossPartialsAsync(
                solution, typeSymbol, existingProperty!, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{@params.TypeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            hostTypeDecl = root.GetAnnotatedNodes(targetTypeAnnotation)
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault()
                ?? throw new RefactoringException(
                    ErrorCodes.SymbolNotFound,
                    $"No type named '{@params.TypeName}' found in the source file.");
        }

        var newTypeDecl = InsertProperty(hostTypeDecl, property);
        // Strip the per-execution annotation so it does not linger in the
        // workspace after commit.
        if (targetTypeAnnotation != null)
            newTypeDecl = (TypeDeclarationSyntax)newTypeDecl.WithoutAnnotations(targetTypeAnnotation);
        var newRoot = root.ReplaceNode(hostTypeDecl, newTypeDecl);
        var newDocument = document.WithSyntaxRoot(newRoot);
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
                Name = propertyName,
                FullyQualifiedName = $"{typeSymbol.ToDisplayString()}.{propertyName}",
                Kind = Contracts.Enums.SymbolKind.Property
            },
            0,
            0);
    }

    internal static void ValidateTypeCanHostProperty(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for generate_property.");
        }
    }

    internal static void ValidateFieldCanBeWrapped(IFieldSymbol field, bool initOnly)
    {
        if (field.IsConst)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Field '{field.Name}' is not a supported target (const fields cannot be wrapped).");
        }

        if (field.IsImplicitlyDeclared)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Field '{field.Name}' is not a supported target.");
        }

        if (field.IsReadOnly && !initOnly)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Field '{field.Name}' is readonly and cannot be assigned from a property setter. Use initOnly or wrap a non-readonly field.");
        }

        if (field.IsReadOnly && field.IsStatic && initOnly)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Static readonly field '{field.Name}' cannot be assigned from a property accessor.");
        }
    }

    internal static void ValidateAutoPropertyAccessors(
        INamedTypeSymbol typeSymbol,
        bool initOnly,
        bool hasBackingField)
    {
        if (hasBackingField)
            return;

        if (!initOnly && typeSymbol.IsReadOnly && typeSymbol.TypeKind == TypeKind.Struct)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Cannot generate a settable auto-property on readonly struct '{typeSymbol.Name}'. Use initOnly.");
        }
    }

    internal static bool ShouldBeStatic(INamedTypeSymbol typeSymbol, IFieldSymbol? backingField) =>
        typeSymbol.IsStatic || backingField is { IsStatic: true };

    internal static PropertyDeclarationSyntax AddStaticModifier(PropertyDeclarationSyntax property)
    {
        if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
            return property;

        var staticToken = SyntaxFactory.Token(SyntaxKind.StaticKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        var modifiers = property.Modifiers;
        var insertIndex = 0;
        for (var i = 0; i < modifiers.Count; i++)
        {
            if (modifiers[i].IsKind(SyntaxKind.PublicKeyword) ||
                modifiers[i].IsKind(SyntaxKind.PrivateKeyword) ||
                modifiers[i].IsKind(SyntaxKind.ProtectedKeyword) ||
                modifiers[i].IsKind(SyntaxKind.InternalKeyword))
            {
                insertIndex = i + 1;
            }
        }

        if (insertIndex == 0 && modifiers.Count > 0)
        {
            var first = modifiers[0];
            staticToken = staticToken.WithLeadingTrivia(first.LeadingTrivia);
            modifiers = modifiers.Replace(first, first.WithLeadingTrivia(SyntaxFactory.TriviaList()));
            return property.WithModifiers(modifiers.Insert(0, staticToken)).NormalizeWhitespace();
        }

        return property.WithModifiers(modifiers.Insert(insertIndex, staticToken)).NormalizeWhitespace();
    }

    /// <summary>
    /// Rejects a name clash, or returns the single existing property that
    /// <c>replaceExisting</c> will remove. Fields, methods, indexers, and
    /// implicit members are never replaced. Two same-named properties with
    /// no single target fail before any write.
    /// </summary>
    internal static IPropertySymbol? ResolvePropertyToReplace(
        INamedTypeSymbol typeSymbol,
        string propertyName,
        bool replaceExisting)
    {
        // GetMembers(name) misses explicit interface properties whose
        // metadata name is "IFoo.Name". Scan all members and match by
        // Name plus explicit-interface implemented names.
        var existing = typeSymbol.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && MemberNameMatches(m, propertyName))
            .ToList();

        var properties = existing
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsIndexer)
            .ToList();

        if (properties.Count >= 2)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Multiple properties named '{propertyName}' exist on '{typeSymbol.Name}'; replaceExisting cannot choose a target.");
        }

        if (properties.Count == 1 && existing.Count == 1 && replaceExisting)
        {
            if (!HasPropertyDeclaration(properties[0]))
            {
                throw new RefactoringException(
                    ErrorCodes.NameCollision,
                    $"A member named '{propertyName}' already exists on '{typeSymbol.Name}'.");
            }

            return properties[0];
        }

        if (existing.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"A member named '{propertyName}' already exists on '{typeSymbol.Name}'.");
        }

        return null;
    }

    internal static bool HasPropertyDeclaration(IPropertySymbol property) =>
        property.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is PropertyDeclarationSyntax);

    internal static bool MemberNameMatches(ISymbol member, string propertyName)
    {
        if (string.Equals(member.Name, propertyName, StringComparison.Ordinal))
            return true;

        return member switch
        {
            IPropertySymbol property => property.ExplicitInterfaceImplementations
                .Any(impl => string.Equals(impl.Name, propertyName, StringComparison.Ordinal)),
            IMethodSymbol method => method.ExplicitInterfaceImplementations
                .Any(impl => string.Equals(impl.Name, propertyName, StringComparison.Ordinal)),
            IEventSymbol @event => @event.ExplicitInterfaceImplementations
                .Any(impl => string.Equals(impl.Name, propertyName, StringComparison.Ordinal)),
            _ => false
        };
    }

    private static IFieldSymbol ResolveBackingField(INamedTypeSymbol typeSymbol, string fieldName)
    {
        var field = typeSymbol.GetMembers(fieldName)
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f => !f.IsImplicitlyDeclared);

        if (field == null)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No field named '{fieldName}' found on type '{typeSymbol.Name}'.");
        }

        return field;
    }

    private static string ResolvePropertyName(GeneratePropertyParams @params, IFieldSymbol? backingField)
    {
        if (!string.IsNullOrWhiteSpace(@params.PropertyName))
            return @params.PropertyName;

        return DerivePropertyName(backingField!.Name);
    }

    internal static string ResolvePropertyType(GeneratePropertyParams @params, IFieldSymbol? backingField)
    {
        if (backingField != null)
        {
            var fieldType = backingField.Type.ToDisplayString();
            if (!string.IsNullOrWhiteSpace(@params.PropertyType) &&
                !PropertyTypeMatchesField(@params.PropertyType, backingField.Type))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidReturnType,
                    $"Property type '{@params.PropertyType}' is incompatible with backing field type '{fieldType}'.");
            }

            return fieldType;
        }

        return @params.PropertyType!;
    }

    internal static bool PropertyTypeMatchesField(string requestedType, ITypeSymbol fieldType)
    {
        requestedType = requestedType.Trim();
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            fieldType.ToDisplayString(),
            fieldType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal),
            fieldType.Name
        };

        if (TryGetSpecialTypeAlias(fieldType.SpecialType, out var alias))
            candidates.Add(alias);

        return candidates.Contains(requestedType);
    }

    private static bool TryGetSpecialTypeAlias(SpecialType specialType, out string alias)
    {
        alias = specialType switch
        {
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_Char => "char",
            SpecialType.System_String => "string",
            SpecialType.System_Object => "object",
            _ => ""
        };
        return alias.Length > 0;
    }

    internal static string DerivePropertyName(string fieldName)
    {
        var name = fieldName.TrimStart('_');
        if (name.Length == 0)
            return "Value";

        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>
    /// Creates an auto-property: <c>{ get; set; }</c> or <c>{ get; init; }</c>.
    /// </summary>
    internal static PropertyDeclarationSyntax CreateAutoProperty(
        string name,
        string type,
        string visibility,
        bool initOnly = false)
    {
        var accessors = new List<AccessorDeclarationSyntax>
        {
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory.AccessorDeclaration(
                    initOnly ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        };

        return SyntaxFactory.PropertyDeclaration(
                SyntaxFactory.ParseTypeName(type).WithTrailingTrivia(SyntaxFactory.Space),
                name)
            .WithModifiers(SyntaxFactory.TokenList(ParseVisibilityTokens(visibility)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates a property with a backing field:
    /// <c>{ get =&gt; _field; set =&gt; _field = value; }</c>.
    /// </summary>
    internal static PropertyDeclarationSyntax CreatePropertyWithBackingField(
        string name,
        string type,
        IFieldSymbol backingField,
        string visibility,
        bool initOnly = false)
    {
        var fieldName = backingField.Name;
        var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName(fieldName)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        var setAccessor = SyntaxFactory.AccessorDeclaration(
                initOnly ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(fieldName),
                    SyntaxFactory.IdentifierName("value"))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return SyntaxFactory.PropertyDeclaration(
                SyntaxFactory.ParseTypeName(type).WithTrailingTrivia(SyntaxFactory.Space),
                name)
            .WithModifiers(SyntaxFactory.TokenList(ParseVisibilityTokens(visibility)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { getAccessor, setAccessor })))
            .NormalizeWhitespace();
    }

    private static TypeDeclarationSyntax InsertProperty(
        TypeDeclarationSyntax typeDeclaration,
        PropertyDeclarationSyntax property)
    {
        var members = typeDeclaration.Members.ToList();
        var insertIndex = 0;

        for (var i = 0; i < members.Count; i++)
        {
            if (members[i] is FieldDeclarationSyntax or PropertyDeclarationSyntax or ConstructorDeclarationSyntax)
                insertIndex = i + 1;
        }

        members.Insert(
            insertIndex,
            property
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    /// <summary>
    /// Removes the matched property declaration from every partial that holds
    /// it. Match by span/kind, not SyntaxNode reference — same seam as
    /// constructor / Equals / ToString / overrides replaceExisting.
    /// Uses <see cref="SyntaxRemoveOptions.KeepExteriorTrivia"/> and
    /// <see cref="SyntaxRemoveOptions.KeepDirectives"/> so a leading
    /// <c>#if</c> / <c>#region</c> on the removed property does not orphan
    /// a following <c>#endif</c> / <c>#endregion</c>.
    /// </summary>
    private static async Task<Solution> RemoveExistingPropertiesAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IPropertySymbol property,
        CancellationToken cancellationToken)
    {
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();

        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            if (syntax is not PropertyDeclarationSyntax)
                continue;
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
            var document = GetDocumentForTree(solution, tree, typeSymbol.Name);
            var treeRoot = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var toRemove = new List<MemberDeclarationSyntax>();
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
            // the removed property does not orphan a following #endif / #endregion.
            var newRoot = treeRoot.RemoveNodes(
                    toRemove,
                    SyntaxRemoveOptions.KeepExteriorTrivia | SyntaxRemoveOptions.KeepDirectives)
                ?? treeRoot;
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

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>BaseTypeDeclarationSyntax</c> <c>FirstOrDefault</c> (enum
    /// participates) and line-only exclusive-end coverage
    /// (<see cref="SpanCoversLine"/>). Do not force column 1 when omitted.
    /// Do not change omitted-line/omitted-column to
    /// <c>TypeDeclarationSyntax</c> / <c>ClassDeclarationSyntax</c>
    /// FirstOrDefault. Column without line keeps today's first-match after
    /// the typeName filter rather than substituting each candidate's own
    /// start line. When column is set with line, picks the type whose
    /// identifier or declaration span covers that 1-based column (same
    /// exclusive-end coverage as
    /// <c>GenerateConstructorOperation.SpanCoversColumn</c> /
    /// <c>GenerateOverridesOperation.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing type. Nested types and
    /// unsupported <c>BaseTypeDeclarationSyntax</c> candidates (enum /
    /// delegate) participate so a covering enum still reaches
    /// <c>InvalidSymbolKind</c> rather than retargeting a later class. Do
    /// not require the declaration to start on <paramref name="line"/>
    /// when column is set — a split declaration may put the identifier on
    /// a continuation line. If column is set with line and nothing covers
    /// that position, return null (TypeNotFound) rather than falling back
    /// to first-match. An empty typeName candidate set also returns null
    /// (<c>hadCandidates</c> false → SymbolNotFound). After
    /// <see cref="RemoveExistingPropertiesAcrossPartialsAsync"/>, recover
    /// the selected type from the per-execution syntax annotation — do not
    /// reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static BaseTypeDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null) =>
        FindTypeDeclaration(root, typeName, line, column, out _);

    /// <inheritdoc cref="FindTypeDeclaration(SyntaxNode, string, int?, int?)"/>
    internal static BaseTypeDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column,
        out bool hadCandidates)
    {
        var candidates = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        hadCandidates = candidates.Count > 0;
        if (!hadCandidates)
            return null;

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter.
        if (column.HasValue && !line.HasValue)
            return candidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Include enum/delegate candidates so a covering
            // enum still reaches InvalidSymbolKind. Do not silently pick
            // the first when a covering node exists elsewhere — scan
            // every candidate. If nothing covers this position, keep
            // today's not-found (null) rather than inventing a first-match.
            return candidates
                .Where(t => TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return candidates.FirstOrDefault();

        // Do not require the declaration to start on `line` — a split
        // type's identifier may live on a continuation line whose
        // declaration span still covers that line. Prefer the identifier
        // hit, then the smallest containing type (nested over outer).
        // Include enum/delegate candidates. Do not silently pick the
        // first when a covering node exists elsewhere — scan every
        // candidate. If nothing covers this line, keep today's
        // first-match rather than inventing a not-found.
        return candidates
            .Where(t => TypeCoversLine(t, line.Value))
            .OrderBy(t => IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? candidates.FirstOrDefault();
    }

    private static bool TypeCoversLine(BaseTypeDeclarationSyntax type, int line) =>
        IdentifierCoversLine(type, line) ||
        SpanCoversLine(type.GetLocation().GetLineSpan(), line);

    private static bool IdentifierCoversLine(BaseTypeDeclarationSyntax type, int line) =>
        SpanCoversLine(type.Identifier.GetLocation().GetLineSpan(), line);

    private static bool TypeCoversColumn(BaseTypeDeclarationSyntax type, int line, int column) =>
        IdentifierCoversColumn(type, line, column) ||
        SpanCoversColumn(type.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(BaseTypeDeclarationSyntax type, int line, int column) =>
        SpanCoversColumn(type.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent type also
    /// match the previous declaration. Same helper as
    /// <c>GenerateConstructorOperation.SpanCoversColumn</c> /
    /// <c>GenerateOverridesOperation.SpanCoversColumn</c>.
    /// </summary>
    internal static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column >= endCol)
            return false;
        return true;
    }

    /// <summary>
    /// 1-based line coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so a span that ends at the start of a line does not
    /// cover that line. Treating the end as inclusive would let the first
    /// line of an adjacent type also match the previous declaration. Same
    /// exclusive-end idea as <c>GenerateConstructorOperation.SpanCoversLine</c>.
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
    /// Creates a preview result with the generated property code.
    /// When replacing a property that lives in another partial, also
    /// includes a Modify pending change per distinct declaring file with
    /// that property as <c>BeforeSnippet</c> — same as constructor
    /// replaceExisting preview.
    /// </summary>
    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        GeneratePropertyParams @params,
        string propertyName,
        PropertyDeclarationSyntax property,
        IPropertySymbol? existingProperty,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var replacing = existingProperty != null;
        var verb = replacing ? "Replace" : "Generate";
        var afterSnippet = property.NormalizeWhitespace().ToFullString();
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"{verb} property '{propertyName}' on {@params.TypeName}",
                BeforeSnippet = replacing
                    ? $"// Type '{@params.TypeName}' (replacing existing property '{propertyName}')"
                    : $"// Type '{@params.TypeName}' (no property '{propertyName}')",
                AfterSnippet = afterSnippet
            }
        };

        if (existingProperty != null)
        {
            var sourcePath = PathResolver.NormalizePath(@params.SourceFile);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in existingProperty.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (syntax is not PropertyDeclarationSyntax existingProp)
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
                    Description = $"Remove existing property '{propertyName}' from {@params.TypeName}",
                    BeforeSnippet = existingProp.NormalizeWhitespace().ToFullString(),
                    AfterSnippet = "// property removed"
                });
            }
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

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

    internal static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.StartsWith('@') && name.Length > 1)
        {
            var bare = name[1..];
            return SyntaxFacts.IsValidIdentifier(bare) ||
                   SyntaxFacts.GetKeywordKind(bare) != SyntaxKind.None;
        }

        if (!SyntaxFacts.IsValidIdentifier(name))
            return false;

        var keywordKind = SyntaxFacts.GetKeywordKind(name);
        return keywordKind == SyntaxKind.None || !SyntaxFacts.IsReservedKeyword(keywordKind);
    }
}

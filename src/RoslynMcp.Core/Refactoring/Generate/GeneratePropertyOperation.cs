using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        var typeDecl = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDecl == null)
        {
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
            return CreatePreviewResult(operationId, @params, propertyName, property, replacing);

        var solution = document.Project.Solution;
        if (replacing)
        {
            solution = await RemoveExistingPropertiesAcrossPartialsAsync(
                solution, typeSymbol, existingProperty!, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{@params.TypeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            hostTypeDecl = FindTypeDeclaration(root, @params.TypeName, hostTypeDecl.SpanStart)
                ?? throw new RefactoringException(
                    ErrorCodes.SymbolNotFound,
                    $"No type named '{@params.TypeName}' found in the source file.");
        }

        var newTypeDecl = InsertProperty(hostTypeDecl, property);
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
            var document = solution.GetDocument(tree)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate a declaring document for type '{typeSymbol.Name}'.");
            var treeRoot = await document.GetSyntaxRootAsync(cancellationToken)
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

            var newRoot = treeRoot.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
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
        GeneratePropertyParams @params,
        string propertyName,
        PropertyDeclarationSyntax property,
        bool replacing)
    {
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

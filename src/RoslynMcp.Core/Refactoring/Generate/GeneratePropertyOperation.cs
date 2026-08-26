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
            ValidateFieldCanBeWrapped(backingField);
        }

        var propertyName = ResolvePropertyName(@params, backingField);
        var propertyType = ResolvePropertyType(@params, backingField);
        ValidateNoNameClash(typeSymbol, propertyName);

        var visibility = string.IsNullOrWhiteSpace(@params.Visibility) ? "public" : @params.Visibility.Trim();
        var property = backingField != null
            ? CreatePropertyWithBackingField(propertyName, propertyType, backingField, visibility, @params.InitOnly)
            : CreateAutoProperty(propertyName, propertyType, visibility, @params.InitOnly);

        if (typeSymbol.IsStatic && !property.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            property = property.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        if (@params.Preview)
            return CreatePreviewResult(operationId, @params, propertyName, property);

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

    internal static void ValidateFieldCanBeWrapped(IFieldSymbol field)
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
    }

    internal static void ValidateNoNameClash(INamedTypeSymbol typeSymbol, string propertyName)
    {
        var existing = typeSymbol.GetMembers(propertyName)
            .FirstOrDefault(m => !m.IsImplicitlyDeclared);
        if (existing != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"A member named '{propertyName}' already exists on '{typeSymbol.Name}'.");
        }
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

    private static string ResolvePropertyType(GeneratePropertyParams @params, IFieldSymbol? backingField)
    {
        if (!string.IsNullOrWhiteSpace(@params.PropertyType))
            return @params.PropertyType;

        return backingField!.Type.ToDisplayString();
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

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        GeneratePropertyParams @params,
        string propertyName,
        PropertyDeclarationSyntax property)
    {
        var afterSnippet = property.NormalizeWhitespace().ToFullString();
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Generate property '{propertyName}' on {@params.TypeName}",
                BeforeSnippet = $"// Type '{@params.TypeName}' (no property '{propertyName}')",
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

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (!char.IsLetter(name[0]) && name[0] != '_')
            return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

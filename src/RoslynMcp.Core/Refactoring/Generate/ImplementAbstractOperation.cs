using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates implementation stubs for unimplemented abstract members inherited
/// by a selected class. Placeholder bodies are
/// <c>throw new global::System.NotImplementedException();</c>.
/// </summary>
public sealed class ImplementAbstractOperation : RefactoringOperationBase<ImplementAbstractParams>
{
    /// <summary>
    /// Creates a new implement abstract operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public ImplementAbstractOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ImplementAbstractParams @params) => Validate(@params);

    /// <summary>
    /// Validates implement-abstract parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ImplementAbstractParams @params)
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
        ImplementAbstractParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var typeDecl = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDecl == null)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No type named '{@params.TypeName}' found in the source file.");
        }

        if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) is not INamedTypeSymbol typeSymbol)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"Could not resolve a symbol for type '{@params.TypeName}'.");
        }

        ValidateTypeCanHostAbstractImplementations(typeSymbol);

        if (typeDecl is not TypeDeclarationSyntax hostTypeDecl)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for implement_abstract.");
        }

        var unimplemented = MemberAnalyzer.GetUnimplementedAbstractMembers(typeSymbol).ToList();

        if (@params.Members != null && @params.Members.Count > 0)
        {
            var requested = new HashSet<string>(@params.Members, StringComparer.Ordinal);
            unimplemented = unimplemented.Where(m => requested.Contains(m.Name)).ToList();
        }

        if (unimplemented.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoUnimplementedAbstractMembers,
                $"Type '{typeSymbol.Name}' has no unimplemented abstract members.");
        }

        var implementations = GenerateImplementations(unimplemented);

        if (@params.Preview)
            return CreatePreviewResult(operationId, @params, unimplemented, implementations);

        var newTypeDeclaration = AddMembers(hostTypeDecl, implementations);
        var newRoot = root.ReplaceNode(hostTypeDecl, newTypeDeclaration);
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
                Name = @params.TypeName,
                FullyQualifiedName = typeSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    internal static void ValidateTypeCanHostAbstractImplementations(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.IsStatic
            || typeSymbol.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface or TypeKind.Struct)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for implement_abstract.");
        }
    }

    private static List<MemberDeclarationSyntax> GenerateImplementations(List<ISymbol> members)
    {
        var implementations = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? impl = member switch
            {
                IMethodSymbol method => CreateMethodStub(method),
                IPropertySymbol property => CreatePropertyStub(property),
                _ => null
            };

            if (impl != null)
                implementations.Add(impl);
        }

        return implementations;
    }

    /// <summary>
    /// Creates an override method stub with a
    /// <c>throw new global::System.NotImplementedException();</c> body.
    /// </summary>
    internal static MethodDeclarationSyntax CreateMethodStub(IMethodSymbol method)
    {
        var parameters = method.Parameters.Select(CreateParameter);
        var methodDecl = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.ParseTypeName(method.ReturnType.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space),
                method.Name)
            .WithModifiers(CreateOverrideModifiers(method.DeclaredAccessibility))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(CreateThrowNotImplementedBody());

        if (method.TypeParameters.Length > 0)
        {
            methodDecl = methodDecl.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(
                    SyntaxFactory.SeparatedList(method.TypeParameters.Select(tp => SyntaxFactory.TypeParameter(tp.Name)))));
        }

        return methodDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an override property stub whose accessors throw
    /// <c>new global::System.NotImplementedException()</c>.
    /// </summary>
    internal static PropertyDeclarationSyntax CreatePropertyStub(IPropertySymbol property)
    {
        var accessors = new List<AccessorDeclarationSyntax>();

        if (property.GetMethod != null)
        {
            accessors.Add(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithBody(CreateThrowNotImplementedBody()));
        }

        if (property.SetMethod != null)
        {
            accessors.Add(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithBody(CreateThrowNotImplementedBody()));
        }

        return SyntaxFactory.PropertyDeclaration(
                SyntaxFactory.ParseTypeName(property.Type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space),
                property.Name)
            .WithModifiers(CreateOverrideModifiers(property.DeclaredAccessibility))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    internal static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("global::System.NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }

    private static ParameterSyntax CreateParameter(IParameterSymbol parameter)
    {
        var syntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name))
            .WithType(SyntaxFactory.ParseTypeName(parameter.Type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space));

        var refKeyword = parameter.RefKind switch
        {
            RefKind.Ref => SyntaxKind.RefKeyword,
            RefKind.Out => SyntaxKind.OutKeyword,
            RefKind.In => SyntaxKind.InKeyword,
            _ => SyntaxKind.None
        };

        if (refKeyword == SyntaxKind.None)
            return syntax;

        return syntax.WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(refKeyword).WithTrailingTrivia(SyntaxFactory.Space)));
    }

    private static SyntaxTokenList CreateOverrideModifiers(Accessibility accessibility)
    {
        var tokens = new List<SyntaxToken>();
        tokens.AddRange(ParseAccessibility(accessibility));
        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        return SyntaxFactory.TokenList(tokens);
    }

    private static IEnumerable<SyntaxToken> ParseAccessibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Protected => new[]
            {
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            Accessibility.Internal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            Accessibility.ProtectedOrInternal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            Accessibility.ProtectedAndInternal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            _ => new[]
            {
                SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            }
        };
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

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ImplementAbstractParams @params,
        List<ISymbol> members,
        List<MemberDeclarationSyntax> implementations)
    {
        var memberNames = string.Join(", ", members.Select(m => m.Name));
        var implCode = string.Join("\n\n",
            implementations.Select(i => i.NormalizeWhitespace().ToFullString()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Implement abstract members on '{@params.TypeName}': {memberNames}",
                BeforeSnippet = $"// End of type '{@params.TypeName}'",
                AfterSnippet = implCode
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

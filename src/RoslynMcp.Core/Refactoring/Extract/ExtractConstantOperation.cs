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

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts a literal expression to a named constant.
/// </summary>
public sealed class ExtractConstantOperation : RefactoringOperationBase<ExtractConstantParams>
{
    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "private", "protected", "internal", "public", "protected internal", "private protected"
    };

    /// <summary>
    /// Creates a new extract constant operation.
    /// </summary>
    public ExtractConstantOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractConstantParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.ConstantName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "constantName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.StartColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (!IsValidIdentifier(@params.ConstantName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid constant name: {@params.ConstantName}");

        if (!ValidVisibilities.Contains(@params.Visibility))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"Invalid visibility: {@params.Visibility}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractConstantParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Get text span from line/column
        var sourceText = await document.GetTextAsync(cancellationToken);
        var startPosition = sourceText.Lines[@params.StartLine - 1].Start + @params.StartColumn - 1;
        var endPosition = sourceText.Lines[@params.EndLine - 1].Start + @params.EndColumn - 1;
        var span = TextSpan.FromBounds(startPosition, endPosition);

        // Find literal at span
        var node = root.FindNode(span);
        var literal = FindLiteralExpression(node, span);

        if (literal == null)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFound,
                "No literal expression found at the specified location.");
        }

        // Check if it's a compile-time constant
        var constantValue = semanticModel.GetConstantValue(literal, cancellationToken);
        if (!constantValue.HasValue)
        {
            throw new RefactoringException(
                ErrorCodes.NotCompileTimeConstant,
                "Expression is not a compile-time constant.");
        }

        // Get the type
        var typeInfo = semanticModel.GetTypeInfo(literal, cancellationToken);
        if (typeInfo.Type == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not determine expression type.");
        }

        // Find containing type
        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            throw new RefactoringException(ErrorCodes.TypeNotFound, "Literal must be inside a type declaration.");
        }

        // Check for name collision
        var existingMember = containingType.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.Text == @params.ConstantName);

        if (existingMember != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Constant '{@params.ConstantName}' already exists in type.");
        }

        // Find all matching literals if ReplaceAll is true
        List<LiteralExpressionSyntax> literalsToReplace;
        if (@params.ReplaceAll)
        {
            literalsToReplace = FindMatchingLiterals(containingType, literal, constantValue.Value);
        }
        else
        {
            literalsToReplace = new List<LiteralExpressionSyntax> { literal };
        }

        // Create constant field
        var constField = CreateConstantField(
            @params.ConstantName,
            @params.Visibility,
            typeInfo.Type,
            literal);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, typeInfo.Type, literalsToReplace.Count, constField);
        }

        // Apply changes
        var constantRef = SyntaxFactory.IdentifierName(@params.ConstantName);

        // Replace all matching literals
        var newRoot = root.ReplaceNodes(
            literalsToReplace,
            (original, rewritten) => constantRef.WithTriviaFrom(original));

        // Find the updated containing type and add constant field
        var updatedContainingType = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == containingType.Identifier.Text);

        var newContainingType = InsertConstantField(updatedContainingType, constField);
        newRoot = newRoot.ReplaceNode(updatedContainingType, newContainingType);

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
                Name = @params.ConstantName,
                FullyQualifiedName = @params.ConstantName,
                Kind = Contracts.Enums.SymbolKind.Constant
            },
            literalsToReplace.Count,
            0);
    }

    private static LiteralExpressionSyntax? FindLiteralExpression(SyntaxNode node, TextSpan span)
    {
        var current = node;
        while (current != null)
        {
            if (current is LiteralExpressionSyntax literal && current.Span.Contains(span))
            {
                return literal;
            }
            current = current.Parent;
        }
        return null;
    }

    private static List<LiteralExpressionSyntax> FindMatchingLiterals(
        TypeDeclarationSyntax containingType,
        LiteralExpressionSyntax originalLiteral,
        object? constantValue)
    {
        return containingType.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(lit =>
            {
                // Match by kind and value
                if (lit.Kind() != originalLiteral.Kind()) return false;

                // Compare token text for precise matching
                return lit.Token.ValueText == originalLiteral.Token.ValueText;
            })
            .ToList();
    }

    private static FieldDeclarationSyntax CreateConstantField(
        string name,
        string visibility,
        ITypeSymbol type,
        LiteralExpressionSyntax initializer)
    {
        var modifiers = new List<SyntaxToken>();

        // Parse visibility
        switch (visibility.ToLowerInvariant())
        {
            case "public":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                break;
            case "protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            case "internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "protected internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "private protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            default:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                break;
        }

        modifiers.Add(SyntaxFactory.Token(SyntaxKind.ConstKeyword));

        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName(type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(name)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(initializer)))))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .NormalizeWhitespace();
    }

    private static TypeDeclarationSyntax InsertConstantField(
        TypeDeclarationSyntax typeDeclaration,
        FieldDeclarationSyntax constField)
    {
        var members = typeDeclaration.Members.ToList();

        // Insert after other constants, or at the beginning
        var insertIndex = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is FieldDeclarationSyntax field &&
                field.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                insertIndex = i + 1;
            }
            else if (insertIndex > 0)
            {
                break;
            }
        }

        members.Insert(insertIndex, constField
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractConstantParams @params,
        ITypeSymbol type,
        int replacementCount,
        FieldDeclarationSyntax constField)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Extract constant '{@params.ConstantName}' ({replacementCount} replacement{(replacementCount > 1 ? "s" : "")})",
                BeforeSnippet = "// (literal values)",
                AfterSnippet = constField.NormalizeWhitespace().ToFullString()
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

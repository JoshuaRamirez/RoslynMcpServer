using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Encapsulate;

/// <summary>
/// Encapsulates a field by converting it to a property.
/// </summary>
public sealed class EncapsulateFieldOperation : RefactoringOperationBase<EncapsulateFieldParams>
{
    /// <summary>
    /// Creates a new encapsulate field operation.
    /// </summary>
    public EncapsulateFieldOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(EncapsulateFieldParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.FieldName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "fieldName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.PropertyName != null && !IsValidIdentifier(@params.PropertyName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid property name: {@params.PropertyName}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        EncapsulateFieldParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find field declaration
        var fieldDeclarator = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v =>
            {
                if (v.Identifier.Text != @params.FieldName) return false;
                // Must be a field, not a local variable
                return v.Parent?.Parent is FieldDeclarationSyntax;
            });

        if (fieldDeclarator == null)
        {
            throw new RefactoringException(
                ErrorCodes.FieldNotFound,
                $"Field '{@params.FieldName}' not found.");
        }

        var fieldDeclaration = (FieldDeclarationSyntax)fieldDeclarator.Parent!.Parent!;
        var fieldSymbol = semanticModel.GetDeclaredSymbol(fieldDeclarator, cancellationToken) as IFieldSymbol;

        if (fieldSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve field symbol.");
        }

        // Check for const
        if (fieldSymbol.IsConst)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot encapsulate const field.");
        }

        // Check for static
        var isStatic = fieldSymbol.IsStatic;

        // Determine property name
        var propertyName = @params.PropertyName ?? DerivePropertyName(@params.FieldName);

        // Check for existing property with same name
        var containingType = fieldDeclarator.Ancestors().OfType<TypeDeclarationSyntax>().First();
        var existingProperty = containingType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName);

        if (existingProperty != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Property '{propertyName}' already exists.");
        }

        // Create property
        var property = SyntaxGenerationHelper.CreatePropertyFromField(
            fieldSymbol,
            propertyName,
            @params.ReadOnly);

        if (isStatic)
        {
            property = property.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        // Find all references to the field
        var references = await SymbolFinder.FindReferencesAsync(
            fieldSymbol,
            Context.Solution,
            cancellationToken);

        var externalReferences = references
            .SelectMany(r => r.Locations)
            .Where(loc => !IsInsideContainingType(loc, fieldSymbol.ContainingType, semanticModel))
            .ToList();

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, fieldSymbol, propertyName, property, externalReferences.Count);
        }

        // Make field private if it's not already
        var newFieldDeclaration = fieldDeclaration;
        if (fieldSymbol.DeclaredAccessibility != Accessibility.Private)
        {
            var newModifiers = SyntaxFactory.TokenList(
                fieldDeclaration.Modifiers.Where(m =>
                    !m.IsKind(SyntaxKind.PublicKeyword) &&
                    !m.IsKind(SyntaxKind.ProtectedKeyword) &&
                    !m.IsKind(SyntaxKind.InternalKeyword))
                .Prepend(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

            newFieldDeclaration = fieldDeclaration.WithModifiers(newModifiers);
        }

        // Rename field if property name would conflict
        string newFieldName = @params.FieldName;
        if (propertyName.Equals(@params.FieldName, StringComparison.OrdinalIgnoreCase))
        {
            newFieldName = "_" + char.ToLowerInvariant(@params.FieldName[0]) + @params.FieldName.Substring(1);

            var newDeclarator = fieldDeclarator.WithIdentifier(SyntaxFactory.Identifier(newFieldName));
            var variableDeclaration = (VariableDeclarationSyntax)fieldDeclarator.Parent!;
            var newVariableDeclaration = variableDeclaration.ReplaceNode(fieldDeclarator, newDeclarator);
            newFieldDeclaration = newFieldDeclaration.WithDeclaration(newVariableDeclaration);

            // Update property to use new field name
            property = UpdatePropertyFieldName(property, newFieldName);
        }

        // Build new type with property added after field
        var newRoot = root;

        // Replace field declaration
        newRoot = newRoot.ReplaceNode(
            newRoot.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .First(f => f.Declaration.Variables.Any(v => v.Identifier.Text == @params.FieldName)),
            newFieldDeclaration);

        // Insert property after field
        var updatedContainingType = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == containingType.Identifier.Text);

        var updatedFieldDecl = updatedContainingType.Members
            .OfType<FieldDeclarationSyntax>()
            .First(f => f.Declaration.Variables.Any(v =>
                v.Identifier.Text == @params.FieldName || v.Identifier.Text == newFieldName));

        var fieldIndex = updatedContainingType.Members.IndexOf(updatedFieldDecl);
        var newMembers = updatedContainingType.Members.Insert(
            fieldIndex + 1,
            property.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                   .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        var newContainingType = updatedContainingType.WithMembers(newMembers);
        newRoot = newRoot.ReplaceNode(updatedContainingType, newContainingType);

        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // Update external references to use property
        foreach (var reference in externalReferences)
        {
            var refDoc = newSolution.GetDocument(reference.Document.Id);
            if (refDoc == null) continue;

            var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
            if (refRoot == null) continue;

            var refNode = refRoot.FindNode(reference.Location.SourceSpan);
            if (refNode is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == @params.FieldName)
            {
                var newIdentifier = SyntaxFactory.IdentifierName(propertyName)
                    .WithTriviaFrom(identifier);
                var newRefRoot = refRoot.ReplaceNode(identifier, newIdentifier);
                newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
            }
        }

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
                Name = propertyName,
                FullyQualifiedName = $"{fieldSymbol.ContainingType.ToDisplayString()}.{propertyName}",
                Kind = Contracts.Enums.SymbolKind.Property
            },
            externalReferences.Count,
            0);
    }

    private static bool IsInsideContainingType(
        ReferenceLocation location,
        INamedTypeSymbol containingType,
        SemanticModel semanticModel)
    {
        // This is a simplified check - in reality we'd need to check if the reference
        // is inside the same type declaration
        return location.Document.FilePath == containingType.Locations.FirstOrDefault()?.SourceTree?.FilePath;
    }

    private static string DerivePropertyName(string fieldName)
    {
        // Remove leading underscore if present
        var name = fieldName.TrimStart('_');

        // Capitalize first letter
        if (name.Length > 0)
        {
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        return name;
    }

    private static PropertyDeclarationSyntax UpdatePropertyFieldName(
        PropertyDeclarationSyntax property,
        string newFieldName)
    {
        return property.ReplaceNodes(
            property.DescendantNodes().OfType<IdentifierNameSyntax>(),
            (original, rewritten) =>
            {
                // Check if this identifier is the field reference in getter/setter
                if (original.Parent is ReturnStatementSyntax ||
                    (original.Parent is AssignmentExpressionSyntax assign && assign.Left == original))
                {
                    return SyntaxFactory.IdentifierName(newFieldName).WithTriviaFrom(original);
                }
                return rewritten;
            });
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        EncapsulateFieldParams @params,
        IFieldSymbol field,
        string propertyName,
        PropertyDeclarationSyntax property,
        int externalRefCount)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Encapsulate field '{@params.FieldName}' as property '{propertyName}' ({externalRefCount} external references to update)",
                BeforeSnippet = $"{field.DeclaredAccessibility.ToString().ToLower()} {field.Type.ToDisplayString()} {@params.FieldName};",
                AfterSnippet = $"private {field.Type.ToDisplayString()} {@params.FieldName};\n\n{property.NormalizeWhitespace()}"
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates Equals() and GetHashCode() overrides for a type.
/// </summary>
public sealed class GenerateEqualsHashCodeOperation : RefactoringOperationBase<GenerateEqualsHashCodeParams>
{
    /// <inheritdoc />
    public GenerateEqualsHashCodeOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateEqualsHashCodeParams @params)
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
        GenerateEqualsHashCodeParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var typeDecl = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDecl == null)
            throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{@params.TypeName}' not found.");

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        // Check for existing overrides
        if (typeSymbol.GetMembers("Equals").Any(m => m is IMethodSymbol method && !method.IsImplicitlyDeclared && method.Parameters.Length == 1))
            throw new RefactoringException(ErrorCodes.AlreadyHasOverride, "Type already has an Equals override.");

        var members = EqualityMemberCollector.CollectMembers(typeSymbol, @params.Fields);
        if (members.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for equality generation.");

        // Generate Equals method
        var equalsMethod = GenerateEquals(@params.TypeName, members);
        var hashCodeMethod = GenerateGetHashCode(members);

        if (@params.Preview)
        {
            var code = equalsMethod.NormalizeWhitespace().ToFullString() + "\n\n" +
                       hashCodeMethod.NormalizeWhitespace().ToFullString();
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = $"Generate Equals and GetHashCode for {@params.TypeName}",
                    BeforeSnippet = $"// Type '{@params.TypeName}' (no Equals/GetHashCode)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newTypeDecl = typeDecl.AddMembers(
            equalsMethod.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed),
            hashCodeMethod.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = @params.TypeName, FullyQualifiedName = @params.TypeName, Kind = Contracts.Enums.SymbolKind.Class },
            0, 0);
    }

    private static MethodDeclarationSyntax GenerateEquals(string typeName, List<ISymbol> members)
    {
        // Build equality comparisons: field1 == other.field1 && field2 == other.field2
        ExpressionSyntax? comparison = null;
        foreach (var member in members)
        {
            var left = SyntaxFactory.IdentifierName(member.Name);
            var right = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("other"),
                SyntaxFactory.IdentifierName(member.Name));

            var memberType = EqualityMemberCollector.GetMemberType(member);
            ExpressionSyntax eq;

            if (memberType.IsReferenceType)
            {
                // EqualityComparer<T>.Default.Equals(field, other.field)
                eq = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.GenericName("EqualityComparer")
                                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                                    SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                                        SyntaxFactory.ParseTypeName(memberType.ToDisplayString())))),
                            SyntaxFactory.IdentifierName("Default")),
                        SyntaxFactory.IdentifierName("Equals")))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[]
                        {
                            SyntaxFactory.Argument(left),
                            SyntaxFactory.Argument(right)
                        })));
            }
            else
            {
                eq = SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, left, right);
            }

            comparison = comparison == null
                ? eq
                : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, comparison, eq);
        }

        // public override bool Equals(object? obj)
        // {
        //     return obj is TypeName other && field1 == other.field1 && ...;
        // }
        var returnExpr = SyntaxFactory.BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("obj"),
                SyntaxFactory.DeclarationPattern(
                    SyntaxFactory.IdentifierName(typeName),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other")))),
            comparison!);

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                "Equals")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("obj"))
                    .WithType(SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))))))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(returnExpr)))
            .NormalizeWhitespace();
    }

    private static MethodDeclarationSyntax GenerateGetHashCode(List<ISymbol> members)
    {
        // Use HashCode.Combine(field1, field2, ...)
        var arguments = members.Select(m =>
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(m.Name)))
            .ToArray();

        ExpressionSyntax hashExpr;
        if (arguments.Length <= 8) // HashCode.Combine supports up to 8 args
        {
            hashExpr = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("HashCode"),
                    SyntaxFactory.IdentifierName("Combine")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        }
        else
        {
            // For > 8 fields, chain XOR
            hashExpr = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(members[0].Name),
                    SyntaxFactory.IdentifierName("GetHashCode")));

            for (int i = 1; i < members.Count; i++)
            {
                var nextHash = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(members[i].Name),
                        SyntaxFactory.IdentifierName("GetHashCode")));

                hashExpr = SyntaxFactory.BinaryExpression(SyntaxKind.ExclusiveOrExpression, hashExpr, nextHash);
            }
        }

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "GetHashCode")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(hashExpr)))
            .NormalizeWhitespace();
    }
}

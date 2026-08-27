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
/// Optionally implements <c>IEquatable&lt;T&gt;</c> with a typed Equals when requested.
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

        if (@params.ImplementIEquatable &&
            (ImplementsIEquatable(typeSymbol) ||
             ListsIEquatable(typeDecl) ||
             HasCompatibleTypedEquals(typeSymbol)))
        {
            throw new RefactoringException(
                ErrorCodes.AlreadyImplementsIEquatable,
                $"Type '{@params.TypeName}' already implements IEquatable<T> or already has a compatible typed Equals.");
        }

        // Check for existing overrides (Equals(object) or any 1-arg Equals when not adding IEquatable)
        if (HasExistingEqualsOverride(typeSymbol))
            throw new RefactoringException(ErrorCodes.AlreadyHasOverride, "Type already has an Equals override.");

        var members = EqualityMemberCollector.CollectMembers(typeSymbol, @params.Fields);
        if (members.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for equality generation.");

        var isValueType = typeSymbol.IsValueType;
        MethodDeclarationSyntax? typedEquals = null;
        MethodDeclarationSyntax objectEquals;
        if (@params.ImplementIEquatable)
        {
            typedEquals = GenerateTypedEquals(@params.TypeName, isValueType, members);
            objectEquals = GenerateDelegatingObjectEquals(@params.TypeName);
        }
        else
        {
            objectEquals = GenerateEquals(@params.TypeName, members);
        }

        var hashCodeMethod = GenerateGetHashCode(members);

        if (@params.Preview)
        {
            var code = BuildPreviewSnippet(@params.TypeName, @params.ImplementIEquatable, typedEquals, objectEquals, hashCodeMethod);
            var description = @params.ImplementIEquatable
                ? $"Generate Equals, GetHashCode, and IEquatable<{@params.TypeName}> for {@params.TypeName}"
                : $"Generate Equals and GetHashCode for {@params.TypeName}";
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = description,
                    BeforeSnippet = $"// Type '{@params.TypeName}' (no Equals/GetHashCode)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newTypeDecl = typeDecl;
        if (@params.ImplementIEquatable)
            newTypeDecl = AddIEquatableInterface(newTypeDecl, @params.TypeName);

        var membersToAdd = new List<MemberDeclarationSyntax>();
        if (typedEquals != null)
        {
            membersToAdd.Add(typedEquals.WithLeadingTrivia(
                SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));
        }

        membersToAdd.Add(objectEquals.WithLeadingTrivia(
            SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));
        membersToAdd.Add(hashCodeMethod.WithLeadingTrivia(
            SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));

        newTypeDecl = newTypeDecl.AddMembers(membersToAdd.ToArray());

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = @params.TypeName, FullyQualifiedName = @params.TypeName, Kind = Contracts.Enums.SymbolKind.Class },
            0, 0);
    }

    private static bool ImplementsIEquatable(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.AllInterfaces.Any(i =>
            i.Name == "IEquatable" &&
            i.ContainingNamespace?.ToDisplayString() == "System" &&
            i.TypeArguments.Length == 1 &&
            SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], typeSymbol));
    }

    private static bool ListsIEquatable(TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl.BaseList == null)
            return false;

        return typeDecl.BaseList.Types.Any(t => IsIEquatableTypeName(t.Type));
    }

    private static bool IsIEquatableTypeName(TypeSyntax type) => type switch
    {
        GenericNameSyntax generic => generic.Identifier.Text == "IEquatable",
        QualifiedNameSyntax qualified => IsIEquatableTypeName(qualified.Right),
        AliasQualifiedNameSyntax alias => IsIEquatableTypeName(alias.Name),
        IdentifierNameSyntax identifier => identifier.Identifier.Text == "IEquatable",
        _ => false
    };

    private static bool HasCompatibleTypedEquals(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetMembers("Equals").OfType<IMethodSymbol>())
        {
            if (member.IsImplicitlyDeclared || member.Parameters.Length != 1)
                continue;

            var paramType = UnwrapNullable(member.Parameters[0].Type);
            if (SymbolEqualityComparer.Default.Equals(paramType, typeSymbol))
                return true;
        }

        return false;
    }

    private static bool HasExistingEqualsOverride(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.GetMembers("Equals").Any(m =>
            m is IMethodSymbol method && !method.IsImplicitlyDeclared && method.Parameters.Length == 1);
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.Length == 1)
        {
            return named.TypeArguments[0];
        }

        return type;
    }

    private static TypeDeclarationSyntax AddIEquatableInterface(TypeDeclarationSyntax typeDecl, string typeName)
    {
        var interfaceType = SyntaxFactory.SimpleBaseType(
            SyntaxFactory.ParseTypeName($"global::System.IEquatable<{typeName}>"));

        if (typeDecl.BaseList == null)
        {
            return typeDecl.WithBaseList(
                SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(interfaceType)));
        }

        return typeDecl.WithBaseList(typeDecl.BaseList.AddTypes(interfaceType));
    }

    private static string BuildPreviewSnippet(
        string typeName,
        bool implementIEquatable,
        MethodDeclarationSyntax? typedEquals,
        MethodDeclarationSyntax objectEquals,
        MethodDeclarationSyntax hashCodeMethod)
    {
        var parts = new List<string>();
        if (implementIEquatable)
            parts.Add($": global::System.IEquatable<{typeName}>");
        if (typedEquals != null)
            parts.Add(typedEquals.NormalizeWhitespace().ToFullString());
        parts.Add(objectEquals.NormalizeWhitespace().ToFullString());
        parts.Add(hashCodeMethod.NormalizeWhitespace().ToFullString());
        return string.Join("\n\n", parts);
    }

    private static ExpressionSyntax BuildMemberComparisons(List<ISymbol> members)
    {
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

        return comparison!;
    }

    private static MethodDeclarationSyntax GenerateTypedEquals(string typeName, bool isValueType, List<ISymbol> members)
    {
        var comparison = BuildMemberComparisons(members);
        ExpressionSyntax returnExpr;
        TypeSyntax parameterType;

        if (isValueType)
        {
            parameterType = SyntaxFactory.IdentifierName(typeName);
            returnExpr = comparison;
        }
        else
        {
            // Person? other — IEquatable<T> for a class uses a nullable parameter.
            parameterType = SyntaxFactory.NullableType(SyntaxFactory.IdentifierName(typeName));
            returnExpr = SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalAndExpression,
                SyntaxFactory.IsPatternExpression(
                    SyntaxFactory.IdentifierName("other"),
                    SyntaxFactory.UnaryPattern(
                        SyntaxFactory.Token(SyntaxKind.NotKeyword),
                        SyntaxFactory.ConstantPattern(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)))),
                comparison);
        }

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                "Equals")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("other"))
                    .WithType(parameterType))))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(returnExpr)))
            .NormalizeWhitespace();
    }

    private static MethodDeclarationSyntax GenerateDelegatingObjectEquals(string typeName)
    {
        // return obj is TypeName other && Equals(other);
        var returnExpr = SyntaxFactory.BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("obj"),
                SyntaxFactory.DeclarationPattern(
                    SyntaxFactory.IdentifierName(typeName),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other")))),
            SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("Equals"))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("other"))))));

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

    private static MethodDeclarationSyntax GenerateEquals(string typeName, List<ISymbol> members)
    {
        var comparison = BuildMemberComparisons(members);

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
            comparison);

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
            // For > 8 fields, use HashCode builder: var hash = new HashCode(); hash.Add(f); ... return hash.ToHashCode();
            var statements = new List<StatementSyntax>();

            // var hash = new HashCode();
            statements.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("hash")
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName("HashCode"))
                                    .WithArgumentList(SyntaxFactory.ArgumentList())))))));

            // hash.Add(fieldN); for each member
            foreach (var member in members)
            {
                statements.Add(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("hash"),
                            SyntaxFactory.IdentifierName("Add")))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(member.Name)))))));
            }

            // return hash.ToHashCode();
            statements.Add(SyntaxFactory.ReturnStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("hash"),
                        SyntaxFactory.IdentifierName("ToHashCode")))));

            return SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                    "GetHashCode")
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithBody(SyntaxFactory.Block(statements))
                .NormalizeWhitespace();
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

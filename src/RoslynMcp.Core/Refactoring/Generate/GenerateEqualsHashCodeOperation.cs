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
/// Optionally implements <c>IEquatable&lt;T&gt;</c> with a typed Equals when requested,
/// optionally generates <c>operator ==</c> / <c>operator !=</c>,
/// and optionally replaces existing equality members when <c>replaceExisting</c> is true.
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

        if (!@params.ReplaceExisting)
        {
            if (@params.ImplementIEquatable &&
                (ImplementsIEquatable(typeSymbol) || HasCompatibleTypedEquals(typeSymbol)))
            {
                throw new RefactoringException(
                    ErrorCodes.AlreadyImplementsIEquatable,
                    $"Type '{@params.TypeName}' already implements IEquatable<T> or already has a compatible typed Equals.");
            }

            if (@params.GenerateOperators && HasExistingEqualityOperators(typeSymbol))
            {
                throw new RefactoringException(
                    ErrorCodes.AlreadyHasEqualityOperators,
                    $"Type '{@params.TypeName}' already declares operator == or operator !=.");
            }

            // Check for existing overrides (Equals(object) or any 1-arg Equals when not adding IEquatable)
            if (HasExistingEqualsOverride(typeSymbol))
                throw new RefactoringException(ErrorCodes.AlreadyHasOverride, "Type already has an Equals override.");
        }

        var members = EqualityMemberCollector.CollectMembers(typeSymbol, @params.Fields);
        if (members.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for equality generation.");

        var selfTypeName = GetSelfTypeName(typeDecl);
        var isValueType = typeSymbol.IsValueType;
        MethodDeclarationSyntax? typedEquals = null;
        MethodDeclarationSyntax objectEquals;
        if (@params.ImplementIEquatable)
        {
            typedEquals = GenerateTypedEquals(selfTypeName, isValueType, members);
            objectEquals = GenerateDelegatingObjectEquals(selfTypeName);
        }
        else
        {
            objectEquals = GenerateEquals(selfTypeName, members);
        }

        var hashCodeMethod = GenerateGetHashCode(members);
        OperatorDeclarationSyntax? equalityOperator = null;
        OperatorDeclarationSyntax? inequalityOperator = null;
        if (@params.GenerateOperators)
        {
            equalityOperator = GenerateEqualityOperator(selfTypeName, isValueType);
            inequalityOperator = GenerateInequalityOperator(selfTypeName, isValueType);
        }

        if (@params.Preview)
        {
            var code = BuildPreviewSnippet(
                selfTypeName,
                @params.ImplementIEquatable,
                typedEquals,
                objectEquals,
                hashCodeMethod,
                equalityOperator,
                inequalityOperator);
            var description = BuildDescription(
                @params.TypeName,
                selfTypeName,
                @params.ImplementIEquatable,
                @params.GenerateOperators,
                @params.ReplaceExisting);
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = description,
                    BeforeSnippet = @params.ReplaceExisting
                        ? $"// Type '{@params.TypeName}' (replacing existing equality members)"
                        : $"// Type '{@params.TypeName}' (no Equals/GetHashCode)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newTypeDecl = typeDecl;
        if (@params.ReplaceExisting)
        {
            newTypeDecl = RemoveExistingEqualityMembers(
                newTypeDecl,
                typeSymbol,
                semanticModel,
                @params.ImplementIEquatable,
                @params.GenerateOperators,
                cancellationToken);
        }

        if (@params.ImplementIEquatable)
            newTypeDecl = AddIEquatableInterface(newTypeDecl, selfTypeName);

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
        if (equalityOperator != null)
        {
            membersToAdd.Add(equalityOperator.WithLeadingTrivia(
                SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));
        }

        if (inequalityOperator != null)
        {
            membersToAdd.Add(inequalityOperator.WithLeadingTrivia(
                SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));
        }

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

    private static bool HasExistingEqualityOperators(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.GetMembers().OfType<IMethodSymbol>().Any(m =>
            m.MethodKind == MethodKind.UserDefinedOperator &&
            (m.Name == "op_Equality" || m.Name == "op_Inequality"));
    }

    private static TypeDeclarationSyntax RemoveExistingEqualityMembers(
        TypeDeclarationSyntax typeDecl,
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        bool implementIEquatable,
        bool generateOperators,
        CancellationToken cancellationToken)
    {
        var remove = new HashSet<SyntaxNode>();

        foreach (var method in typeSymbol.GetMembers("Equals").OfType<IMethodSymbol>())
        {
            if (method.IsImplicitlyDeclared || method.Parameters.Length != 1)
                continue;

            var paramType = UnwrapNullable(method.Parameters[0].Type);
            var isObjectEquals = paramType.SpecialType == SpecialType.System_Object;
            var isTypedEquals = implementIEquatable &&
                SymbolEqualityComparer.Default.Equals(paramType, typeSymbol);

            if (!isObjectEquals && !isTypedEquals)
                continue;

            var syntax = GetDeclaredMemberInType(method, typeDecl, cancellationToken);
            if (syntax != null)
                remove.Add(syntax);
        }

        foreach (var method in typeSymbol.GetMembers("GetHashCode").OfType<IMethodSymbol>())
        {
            if (method.IsImplicitlyDeclared || method.IsStatic || method.Parameters.Length != 0)
                continue;

            var syntax = GetDeclaredMemberInType(method, typeDecl, cancellationToken);
            if (syntax != null)
                remove.Add(syntax);
        }

        if (generateOperators)
        {
            foreach (var method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.UserDefinedOperator)
                    continue;
                if (method.Name is not ("op_Equality" or "op_Inequality"))
                    continue;

                var syntax = GetDeclaredMemberInType(method, typeDecl, cancellationToken);
                if (syntax != null)
                    remove.Add(syntax);
            }
        }

        var result = typeDecl;
        if (implementIEquatable)
            result = StripIEquatableInterface(result, typeSymbol, semanticModel, cancellationToken);

        if (remove.Count == 0)
            return result;

        var remaining = result.Members.Where(m => !remove.Contains(m)).ToArray();
        return result.WithMembers(SyntaxFactory.List(remaining));
    }

    private static SyntaxNode? GetDeclaredMemberInType(
        ISymbol symbol,
        TypeDeclarationSyntax typeDecl,
        CancellationToken cancellationToken)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(cancellationToken);
            if (syntax.Parent == typeDecl)
                return syntax;
        }

        return null;
    }

    private static TypeDeclarationSyntax StripIEquatableInterface(
        TypeDeclarationSyntax typeDecl,
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (typeDecl.BaseList == null)
            return typeDecl;

        var remaining = new List<BaseTypeSyntax>();
        var removed = false;
        foreach (var baseType in typeDecl.BaseList.Types)
        {
            if (IsSystemIEquatableOfSelf(baseType.Type, typeSymbol, semanticModel, cancellationToken))
            {
                removed = true;
                continue;
            }

            remaining.Add(baseType);
        }

        if (!removed)
            return typeDecl;

        if (remaining.Count == 0)
            return typeDecl.WithBaseList(null);

        return typeDecl.WithBaseList(
            typeDecl.BaseList.WithTypes(SyntaxFactory.SeparatedList(remaining)));
    }

    private static bool IsSystemIEquatableOfSelf(
        TypeSyntax typeSyntax,
        INamedTypeSymbol selfType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type as INamedTypeSymbol
            ?? semanticModel.GetSymbolInfo(typeSyntax, cancellationToken).Symbol as INamedTypeSymbol;
        if (symbol == null)
            return false;

        return symbol.Name == "IEquatable"
            && symbol.ContainingNamespace?.ToDisplayString() == "System"
            && symbol.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(symbol.TypeArguments[0], selfType);
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

    /// <summary>
    /// Identifier plus type parameters from the declaration (e.g. <c>Person</c>, <c>Box&lt;T&gt;</c>, <c>Pair&lt;T, U&gt;</c>).
    /// Lookup uses the bare identifier; generated IEquatable/Equals must keep the type arguments.
    /// </summary>
    private static string GetSelfTypeName(TypeDeclarationSyntax typeDecl)
    {
        var identifier = typeDecl.Identifier.Text;
        if (typeDecl.TypeParameterList == null || typeDecl.TypeParameterList.Parameters.Count == 0)
            return identifier;

        var arguments = string.Join(", ", typeDecl.TypeParameterList.Parameters.Select(p => p.Identifier.Text));
        return $"{identifier}<{arguments}>";
    }

    private static TypeSyntax SelfTypeSyntax(string selfTypeName) =>
        SyntaxFactory.ParseTypeName(selfTypeName);

    private static TypeDeclarationSyntax AddIEquatableInterface(TypeDeclarationSyntax typeDecl, string selfTypeName)
    {
        var interfaceType = SyntaxFactory.SimpleBaseType(
            SyntaxFactory.ParseTypeName($"global::System.IEquatable<{selfTypeName}>"));

        if (typeDecl.BaseList == null)
        {
            return typeDecl.WithBaseList(
                SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(interfaceType)));
        }

        return typeDecl.WithBaseList(typeDecl.BaseList.AddTypes(interfaceType));
    }

    private static string BuildDescription(
        string typeName,
        string selfTypeName,
        bool implementIEquatable,
        bool generateOperators,
        bool replaceExisting)
    {
        var verb = replaceExisting ? "Replace" : "Generate";
        if (implementIEquatable && generateOperators)
            return $"{verb} Equals, GetHashCode, IEquatable<{selfTypeName}>, and equality operators for {typeName}";
        if (implementIEquatable)
            return $"{verb} Equals, GetHashCode, and IEquatable<{selfTypeName}> for {typeName}";
        if (generateOperators)
            return $"{verb} Equals, GetHashCode, and equality operators for {typeName}";
        return $"{verb} Equals and GetHashCode for {typeName}";
    }

    private static string BuildPreviewSnippet(
        string typeName,
        bool implementIEquatable,
        MethodDeclarationSyntax? typedEquals,
        MethodDeclarationSyntax objectEquals,
        MethodDeclarationSyntax hashCodeMethod,
        OperatorDeclarationSyntax? equalityOperator,
        OperatorDeclarationSyntax? inequalityOperator)
    {
        var parts = new List<string>();
        if (implementIEquatable)
            parts.Add($": global::System.IEquatable<{typeName}>");
        if (typedEquals != null)
            parts.Add(typedEquals.NormalizeWhitespace().ToFullString());
        parts.Add(objectEquals.NormalizeWhitespace().ToFullString());
        parts.Add(hashCodeMethod.NormalizeWhitespace().ToFullString());
        if (equalityOperator != null)
            parts.Add(equalityOperator.NormalizeWhitespace().ToFullString());
        if (inequalityOperator != null)
            parts.Add(inequalityOperator.NormalizeWhitespace().ToFullString());
        return string.Join("\n\n", parts);
    }

    private static TypeSyntax OperatorParameterType(string selfTypeName, bool isValueType)
    {
        return isValueType
            ? SelfTypeSyntax(selfTypeName)
            : SyntaxFactory.NullableType(SelfTypeSyntax(selfTypeName));
    }

    private static OperatorDeclarationSyntax GenerateEqualityOperator(string selfTypeName, bool isValueType)
    {
        // return global::System.Object.Equals(left, right);
        // Qualify so an existing two-arg Equals on the type cannot steal the call.
        var returnExpr = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName("global::System.Object"),
                    SyntaxFactory.IdentifierName("Equals")))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("left")),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("right"))
            })));

        return SyntaxFactory.OperatorDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("left"))
                    .WithType(OperatorParameterType(selfTypeName, isValueType)),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("right"))
                    .WithType(OperatorParameterType(selfTypeName, isValueType))
            })))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(returnExpr)))
            .NormalizeWhitespace();
    }

    private static OperatorDeclarationSyntax GenerateInequalityOperator(string selfTypeName, bool isValueType)
    {
        // return !(left == right);
        var returnExpr = SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    SyntaxFactory.IdentifierName("left"),
                    SyntaxFactory.IdentifierName("right"))));

        return SyntaxFactory.OperatorDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                SyntaxFactory.Token(SyntaxKind.ExclamationEqualsToken))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("left"))
                    .WithType(OperatorParameterType(selfTypeName, isValueType)),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("right"))
                    .WithType(OperatorParameterType(selfTypeName, isValueType))
            })))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(returnExpr)))
            .NormalizeWhitespace();
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

    private static MethodDeclarationSyntax GenerateTypedEquals(string selfTypeName, bool isValueType, List<ISymbol> members)
    {
        var comparison = BuildMemberComparisons(members);
        ExpressionSyntax returnExpr;
        TypeSyntax parameterType;

        if (isValueType)
        {
            parameterType = SelfTypeSyntax(selfTypeName);
            returnExpr = comparison;
        }
        else
        {
            // Person? other — IEquatable<T> for a class uses a nullable parameter.
            parameterType = SyntaxFactory.NullableType(SelfTypeSyntax(selfTypeName));
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

    private static MethodDeclarationSyntax GenerateDelegatingObjectEquals(string selfTypeName)
    {
        // return obj is TypeName other && Equals(other);
        var returnExpr = SyntaxFactory.BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("obj"),
                SyntaxFactory.DeclarationPattern(
                    SelfTypeSyntax(selfTypeName),
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

    private static MethodDeclarationSyntax GenerateEquals(string selfTypeName, List<ISymbol> members)
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
                    SelfTypeSyntax(selfTypeName),
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

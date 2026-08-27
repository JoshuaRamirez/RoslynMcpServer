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
/// optionally replaces existing equality members when <c>replaceExisting</c> is true,
/// honors <c>useHashCodeCombine</c> for the GetHashCode body shape,
/// honors <c>includeProperties</c> when collecting equality members,
/// and honors <c>callSuper</c> to fold the immediate base type's equality into Equals/GetHashCode.
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

        if (@params.CallSuper && IsObjectOrValueTypeBase(typeSymbol))
        {
            throw new RefactoringException(
                ErrorCodes.CallSuperOnObjectBase,
                "callSuper cannot be used when the immediate base type is System.Object or System.ValueType.");
        }

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

        var members = EqualityMemberCollector.CollectMembers(typeSymbol, @params.Fields, @params.IncludeProperties);
        if (members.Count == 0 && !@params.CallSuper)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for equality generation.");

        var selfTypeName = GetSelfTypeName(typeDecl);
        var isValueType = typeSymbol.IsValueType;
        MethodDeclarationSyntax? typedEquals = null;
        MethodDeclarationSyntax objectEquals;
        if (@params.ImplementIEquatable)
        {
            typedEquals = GenerateTypedEquals(selfTypeName, isValueType, members, @params.CallSuper);
            objectEquals = GenerateDelegatingObjectEquals(selfTypeName);
        }
        else
        {
            objectEquals = GenerateEquals(selfTypeName, members, @params.CallSuper);
        }

        var hashCodeMethod = GenerateGetHashCode(members, @params.UseHashCodeCombine, @params.CallSuper);
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
                @params.ReplaceExisting,
                @params.UseHashCodeCombine,
                @params.CallSuper,
                members.Count);
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

        var solution = document.Project.Solution;
        if (@params.ReplaceExisting)
        {
            solution = await RemoveExistingEqualityMembersAcrossPartialsAsync(
                solution,
                typeSymbol,
                @params.ImplementIEquatable,
                @params.GenerateOperators,
                cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{@params.TypeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            typeDecl = FindTypeDeclaration(root, @params.TypeName, typeDecl.SpanStart)
                ?? throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{@params.TypeName}' not found.");
        }

        var newTypeDecl = typeDecl;
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

    private static async Task<Solution> RemoveExistingEqualityMembersAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        bool implementIEquatable,
        bool generateOperators,
        CancellationToken cancellationToken)
    {
        // Match by span/kind, not SyntaxNode reference. WithBaseList rebuilds child
        // red nodes, so a HashSet<SyntaxNode> would miss after an IEquatable strip.
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();

        foreach (var method in CollectEqualityMembersToReplace(typeSymbol, implementIEquatable, generateOperators))
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
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
        }

        var treesToEdit = new HashSet<SyntaxTree>(membersByTreeAndPart.Keys);
        if (implementIEquatable)
        {
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
                treesToEdit.Add(reference.SyntaxTree);
        }

        foreach (var tree in treesToEdit)
        {
            var document = solution.GetDocument(tree)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate a declaring document for type '{typeSymbol.Name}'.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            var semanticModel = implementIEquatable
                ? await document.GetSemanticModelAsync(cancellationToken)
                : null;

            var replacements = new Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree != tree)
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax part)
                    continue;

                var updated = part;
                if (membersByTreeAndPart.TryGetValue(tree, out var byPart) &&
                    byPart.TryGetValue(part.SpanStart, out var keys) &&
                    keys.Count > 0)
                {
                    var remainingMembers = updated.Members
                        .Where(m => !keys.Contains((m.SpanStart, m.Span.End, m.Kind())))
                        .ToArray();
                    updated = updated.WithMembers(SyntaxFactory.List(remainingMembers));
                }

                if (implementIEquatable && semanticModel != null)
                    updated = StripIEquatableInterface(part, updated, typeSymbol, semanticModel, cancellationToken);

                if (!ReferenceEquals(updated, part))
                    replacements[part] = updated;
            }

            if (replacements.Count == 0)
                continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    private static IEnumerable<IMethodSymbol> CollectEqualityMembersToReplace(
        INamedTypeSymbol typeSymbol,
        bool implementIEquatable,
        bool generateOperators)
    {
        foreach (var method in typeSymbol.GetMembers("Equals").OfType<IMethodSymbol>())
        {
            if (method.IsImplicitlyDeclared || method.Parameters.Length != 1)
                continue;

            var paramType = UnwrapNullable(method.Parameters[0].Type);
            var isObjectEquals = paramType.SpecialType == SpecialType.System_Object;
            var isTypedEquals = implementIEquatable &&
                SymbolEqualityComparer.Default.Equals(paramType, typeSymbol);
            if (isObjectEquals || isTypedEquals)
                yield return method;
        }

        foreach (var method in typeSymbol.GetMembers("GetHashCode").OfType<IMethodSymbol>())
        {
            if (method.IsImplicitlyDeclared || method.IsStatic || method.Parameters.Length != 0)
                continue;

            yield return method;
        }

        if (!generateOperators)
            yield break;

        foreach (var method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.UserDefinedOperator)
                continue;
            if (method.Name is not ("op_Equality" or "op_Inequality"))
                continue;

            yield return method;
        }
    }

    private static TypeDeclarationSyntax? FindTypeDeclaration(SyntaxNode root, string typeName, int preferredSpanStart)
    {
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();
        return matches.FirstOrDefault(t => t.SpanStart == preferredSpanStart) ?? matches.FirstOrDefault();
    }

    private static TypeDeclarationSyntax StripIEquatableInterface(
        TypeDeclarationSyntax original,
        TypeDeclarationSyntax current,
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (original.BaseList == null || current.BaseList == null)
            return current;

        var removeIndexes = new HashSet<int>();
        for (var i = 0; i < original.BaseList.Types.Count; i++)
        {
            if (IsSystemIEquatableOfSelf(original.BaseList.Types[i].Type, typeSymbol, semanticModel, cancellationToken))
                removeIndexes.Add(i);
        }

        if (removeIndexes.Count == 0)
            return current;

        var remaining = current.BaseList.Types
            .Where((_, i) => !removeIndexes.Contains(i))
            .ToArray();

        if (remaining.Length == 0)
            return current.WithBaseList(null);

        return current.WithBaseList(
            current.BaseList.WithTypes(SyntaxFactory.SeparatedList(remaining)));
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

    private static bool IsObjectOrValueTypeBase(INamedTypeSymbol typeSymbol)
    {
        var baseType = typeSymbol.BaseType;
        return baseType == null
            || baseType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;
    }

    private static string BuildDescription(
        string typeName,
        string selfTypeName,
        bool implementIEquatable,
        bool generateOperators,
        bool replaceExisting,
        bool useHashCodeCombine,
        bool callSuper,
        int memberCount)
    {
        var verb = replaceExisting ? "Replace" : "Generate";
        var hashArgCount = memberCount + (callSuper ? 1 : 0);
        var hashStyle = useHashCodeCombine
            ? (hashArgCount <= 8 ? "HashCode.Combine" : "HashCode builder")
            : "unchecked prime-multiply";
        var baseNote = callSuper ? " including base equality" : "";
        if (implementIEquatable && generateOperators)
            return $"{verb} Equals, GetHashCode ({hashStyle}), IEquatable<{selfTypeName}>, and equality operators{baseNote} for {typeName}";
        if (implementIEquatable)
            return $"{verb} Equals, GetHashCode ({hashStyle}), and IEquatable<{selfTypeName}>{baseNote} for {typeName}";
        if (generateOperators)
            return $"{verb} Equals, GetHashCode ({hashStyle}), and equality operators{baseNote} for {typeName}";
        return $"{verb} Equals and GetHashCode ({hashStyle}){baseNote} for {typeName}";
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

    private static ExpressionSyntax? BuildMemberComparisons(List<ISymbol> members)
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

        return comparison;
    }

    private static ExpressionSyntax AndAlso(params ExpressionSyntax?[] parts)
    {
        ExpressionSyntax? result = null;
        foreach (var part in parts)
        {
            if (part == null)
                continue;

            result = result == null
                ? part
                : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, result, part);
        }

        return result ?? throw new InvalidOperationException("Equals body requires at least one condition.");
    }

    private static InvocationExpressionSyntax BaseEqualsCall() =>
        SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.BaseExpression(),
                    SyntaxFactory.IdentifierName("Equals")))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("other")))));

    private static InvocationExpressionSyntax BaseGetHashCodeCall() =>
        SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.BaseExpression(),
                SyntaxFactory.IdentifierName("GetHashCode")));

    private static MethodDeclarationSyntax GenerateTypedEquals(
        string selfTypeName,
        bool isValueType,
        List<ISymbol> members,
        bool callSuper)
    {
        var comparison = BuildMemberComparisons(members);
        var baseEquals = callSuper ? BaseEqualsCall() : null;
        ExpressionSyntax returnExpr;
        TypeSyntax parameterType;

        if (isValueType)
        {
            parameterType = SelfTypeSyntax(selfTypeName);
            returnExpr = AndAlso(baseEquals, comparison);
        }
        else
        {
            // Person? other — IEquatable<T> for a class uses a nullable parameter.
            parameterType = SyntaxFactory.NullableType(SelfTypeSyntax(selfTypeName));
            var notNull = SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("other"),
                SyntaxFactory.UnaryPattern(
                    SyntaxFactory.Token(SyntaxKind.NotKeyword),
                    SyntaxFactory.ConstantPattern(
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
            returnExpr = AndAlso(notNull, baseEquals, comparison);
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

    private static MethodDeclarationSyntax GenerateEquals(string selfTypeName, List<ISymbol> members, bool callSuper)
    {
        var comparison = BuildMemberComparisons(members);

        // public override bool Equals(object? obj)
        // {
        //     return obj is TypeName other && [base.Equals(other) &&] field1 == other.field1 && ...;
        // }
        var typeCheck = SyntaxFactory.IsPatternExpression(
            SyntaxFactory.IdentifierName("obj"),
            SyntaxFactory.DeclarationPattern(
                SelfTypeSyntax(selfTypeName),
                SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other"))));
        var returnExpr = AndAlso(typeCheck, callSuper ? BaseEqualsCall() : null, comparison);

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

    private static MethodDeclarationSyntax GenerateGetHashCode(List<ISymbol> members, bool useHashCodeCombine, bool callSuper)
    {
        return useHashCodeCombine
            ? GenerateHashCodeCombineGetHashCode(members, callSuper)
            : GeneratePrimeMultiplyGetHashCode(members, callSuper);
    }

    private static TypeSyntax SystemHashCodeType() =>
        SyntaxFactory.ParseTypeName("global::System.HashCode");

    private static MethodDeclarationSyntax GenerateHashCodeCombineGetHashCode(List<ISymbol> members, bool callSuper)
    {
        // Qualify System.HashCode so a type-local HashCode cannot steal the call.
        var arguments = new List<ArgumentSyntax>();
        if (callSuper)
            arguments.Add(SyntaxFactory.Argument(BaseGetHashCodeCall()));
        arguments.AddRange(members.Select(m => SyntaxFactory.Argument(InstanceMemberAccess(m.Name))));

        if (arguments.Count <= 8) // HashCode.Combine supports up to 8 args
        {
            var hashExpr = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SystemHashCodeType(),
                    SyntaxFactory.IdentifierName("Combine")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

            return GetHashCodeMethod(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(hashExpr)));
        }

        // For > 8 fields: var hash = new global::System.HashCode(); hash.Add(f); ... return hash.ToHashCode();
        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("hash")
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(SystemHashCodeType())
                                    .WithArgumentList(SyntaxFactory.ArgumentList()))))))
        };

        if (callSuper)
        {
            statements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("hash"),
                        SyntaxFactory.IdentifierName("Add")))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(BaseGetHashCodeCall()))))));
        }

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
                            SyntaxFactory.Argument(InstanceMemberAccess(member.Name)))))));
        }

        statements.Add(SyntaxFactory.ReturnStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("hash"),
                    SyntaxFactory.IdentifierName("ToHashCode")))));

        return GetHashCodeMethod(SyntaxFactory.Block(statements));
    }

    private static MethodDeclarationSyntax GeneratePrimeMultiplyGetHashCode(List<ISymbol> members, bool callSuper)
    {
        ExpressionSyntax seed = callSuper
            ? BaseGetHashCodeCall()
            : SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(17));

        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("hash")
                            .WithInitializer(SyntaxFactory.EqualsValueClause(seed)))))
        };

        foreach (var member in members)
        {
            var multiply = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.MultiplyExpression,
                    SyntaxFactory.IdentifierName("hash"),
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(31))));

            statements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("hash"),
                    SyntaxFactory.BinaryExpression(
                        SyntaxKind.AddExpression,
                        multiply,
                        BuildMemberHashExpression(member)))));
        }

        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("hash")));

        var uncheckedBlock = SyntaxFactory.CheckedStatement(
            SyntaxKind.UncheckedStatement,
            SyntaxFactory.Block(statements));

        return GetHashCodeMethod(SyntaxFactory.Block(uncheckedBlock));
    }

    private static ExpressionSyntax InstanceMemberAccess(string memberName) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.IdentifierName(memberName));

    private static ExpressionSyntax BuildMemberHashExpression(ISymbol member)
    {
        var memberType = EqualityMemberCollector.GetMemberType(member);
        // Qualify with this. so a member named hash is not shadowed by the local.
        var memberAccess = InstanceMemberAccess(member.Name);

        if (!NeedsNullSafeHash(memberType))
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    memberAccess,
                    SyntaxFactory.IdentifierName("GetHashCode")));
        }

        // (this.Member?.GetHashCode() ?? 0) — parenthesized so ?? does not bind across +.
        var conditionalAccess = SyntaxFactory.ConditionalAccessExpression(
            memberAccess,
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberBindingExpression(SyntaxFactory.IdentifierName("GetHashCode"))));

        return SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.CoalesceExpression,
                conditionalAccess,
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(0))));
    }

    private static bool NeedsNullSafeHash(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return true;
        }

        if (type.IsValueType)
            return type.NullableAnnotation == NullableAnnotation.Annotated;

        // Nullable reference types and oblivious/unconstrained references under NRT.
        return type.NullableAnnotation != NullableAnnotation.NotAnnotated;
    }

    private static MethodDeclarationSyntax GetHashCodeMethod(BlockSyntax body)
    {
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "GetHashCode")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithBody(body)
            .NormalizeWhitespace();
    }
}

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
/// Generates a method declaration from an undefined call site, inferring the
/// signature from usage. The placeholder body is
/// <c>throw new NotImplementedException();</c>.
/// </summary>
public sealed class GenerateMethodStubOperation : RefactoringOperationBase<GenerateMethodStubParams>
{
    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "private", "protected", "internal", "protected internal", "private protected"
    };

    /// <summary>
    /// Creates a new generate method stub operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public GenerateMethodStubOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateMethodStubParams @params) => Validate(@params);

    /// <summary>
    /// Validates generate-method-stub parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(GenerateMethodStubParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (@params.MethodName != null && !IsValidIdentifier(@params.MethodName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid method name: {@params.MethodName}");

        if (!string.IsNullOrWhiteSpace(@params.ReturnType) && !IsValidTypeName(@params.ReturnType))
            throw new RefactoringException(ErrorCodes.InvalidReturnType, $"Invalid return type: {@params.ReturnType}");

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
        GenerateMethodStubParams @params,
        CancellationToken cancellationToken)
    {
        var callSiteDocument = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(callSiteDocument, Context.Workspace);

        var root = await callSiteDocument.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await callSiteDocument.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var position = SymbolResolver.GetPosition(root, @params.Line, @params.Column);
        var invocation = FindInvocationAtPosition(root, position);
        if (invocation == null)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"No method invocation found at line {@params.Line}, column {@params.Column}.");
        }

        var methodName = ResolveMethodName(@params, invocation);
        var callSiteType = GetEnclosingType(invocation, semanticModel, cancellationToken);
        var target = ResolveTarget(invocation, semanticModel, callSiteType, cancellationToken);
        ValidateTypeCanHostMethod(target.Type);

        var targetDeclaration = await GetEditableTypeDeclarationAsync(target.Type, cancellationToken);
        var targetDocument = Context.Solution.GetDocument(targetDeclaration.SyntaxTree)
            ?? throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Target type '{target.Type.Name}' is not part of the workspace.");
        ValidateDocumentIsEditable(targetDocument, Context.Workspace);

        var parameters = InferParameters(invocation, semanticModel);
        ValidateNoCompatibleMethod(target.Type, methodName, parameters);

        var returnType = InferReturnType(invocation, semanticModel, @params, cancellationToken);
        var isAsync = @params.GenerateAsync || IsAwaited(invocation);
        if (isAsync)
            returnType = ToAsyncReturnType(returnType);

        var isStatic = ShouldBeStatic(target, callSiteType, invocation);
        var visibility = ResolveVisibility(@params, target.SameTypeAsCaller);
        var method = CreateMethodStub(methodName, returnType, parameters, visibility, isStatic, isAsync);

        if (@params.Preview)
            return CreatePreviewResult(operationId, targetDocument, target.Type.Name, methodName, method);

        var newTypeDecl = InsertMethod(targetDeclaration, method);
        Solution newSolution;
        if (targetDocument.Id == callSiteDocument.Id)
        {
            var targetInCallSite = (TypeDeclarationSyntax)root.FindNode(targetDeclaration.Span);
            var newRoot = root.ReplaceNode(targetInCallSite, newTypeDecl);
            newSolution = callSiteDocument.WithSyntaxRoot(newRoot).Project.Solution;
        }
        else
        {
            var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse target file.");
            var currentDecl = (TypeDeclarationSyntax)targetRoot.FindNode(targetDeclaration.Span);
            var newRoot = targetRoot.ReplaceNode(currentDecl, newTypeDecl);
            newSolution = targetDocument.WithSyntaxRoot(newRoot).Project.Solution;
        }

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
                Name = methodName,
                FullyQualifiedName = $"{target.Type.ToDisplayString()}.{methodName}",
                Kind = Contracts.Enums.SymbolKind.Method
            },
            0,
            0);
    }

    internal static InvocationExpressionSyntax? FindInvocationAtPosition(SyntaxNode root, int position)
    {
        var token = root.FindToken(position);
        if (token == default)
            return null;

        foreach (var node in token.Parent?.AncestorsAndSelf() ?? Enumerable.Empty<SyntaxNode>())
        {
            if (node is InvocationExpressionSyntax invocation)
            {
                var name = GetInvokedName(invocation);
                if (name != null && name.Span.Start <= position && position <= name.Span.End)
                    return invocation;
            }
        }

        return null;
    }

    internal static SimpleNameSyntax? GetInvokedName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            SimpleNameSyntax simple => simple,
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            _ => null
        };
    }

    private static string ResolveMethodName(GenerateMethodStubParams @params, InvocationExpressionSyntax invocation)
    {
        if (!string.IsNullOrWhiteSpace(@params.MethodName))
            return @params.MethodName;

        var name = GetInvokedName(invocation);
        if (name == null || string.IsNullOrWhiteSpace(name.Identifier.ValueText))
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                "No method invocation found at the specified location.");
        }

        return name.Identifier.ValueText;
    }

    private static INamedTypeSymbol GetEnclosingType(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var typeDecl = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                "Call site is not inside a type that can host a generated method.");
        }

        if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) is not INamedTypeSymbol typeSymbol)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Could not resolve a symbol for type '{typeDecl.Identifier.Text}'.");
        }

        return typeSymbol;
    }

    internal static TargetResolution ResolveTarget(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        INamedTypeSymbol enclosingType,
        CancellationToken cancellationToken)
    {
        switch (invocation.Expression)
        {
            case SimpleNameSyntax:
                return new TargetResolution(enclosingType, SameTypeAsCaller: true, ReceiverIsTypeName: IsInStaticContext(invocation, semanticModel, cancellationToken));

            case MemberAccessExpressionSyntax member:
                return ResolveReceiver(member.Expression, semanticModel, enclosingType, cancellationToken);

            case MemberBindingExpressionSyntax:
                if (invocation.Parent is ConditionalAccessExpressionSyntax conditional)
                    return ResolveReceiver(conditional.Expression, semanticModel, enclosingType, cancellationToken);
                break;
        }

        throw new RefactoringException(
            ErrorCodes.TypeNotFound,
            "Cannot determine the target type for the method from the call-site receiver.");
    }

    private static TargetResolution ResolveReceiver(
        ExpressionSyntax receiver,
        SemanticModel semanticModel,
        INamedTypeSymbol enclosingType,
        CancellationToken cancellationToken)
    {
        if (receiver is ThisExpressionSyntax)
            return new TargetResolution(enclosingType, SameTypeAsCaller: true, ReceiverIsTypeName: false);

        if (receiver is BaseExpressionSyntax)
        {
            var baseType = enclosingType.BaseType;
            if (baseType == null || baseType.SpecialType == SpecialType.System_Object)
            {
                throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    "Cannot determine a target type from the base receiver.");
            }

            return new TargetResolution(baseType, SameTypeAsCaller: false, ReceiverIsTypeName: false);
        }

        var symbolInfo = semanticModel.GetSymbolInfo(receiver, cancellationToken);
        if (symbolInfo.Symbol is INamedTypeSymbol typeSymbol && typeSymbol.TypeKind != TypeKind.Error)
            return new TargetResolution(typeSymbol, IsSameType(typeSymbol, enclosingType), ReceiverIsTypeName: true);

        if (symbolInfo.Symbol is IAliasSymbol { Target: INamedTypeSymbol aliased } && aliased.TypeKind != TypeKind.Error)
            return new TargetResolution(aliased, IsSameType(aliased, enclosingType), ReceiverIsTypeName: true);

        var receiverType = semanticModel.GetTypeInfo(receiver, cancellationToken).Type
            ?? semanticModel.GetTypeInfo(receiver, cancellationToken).ConvertedType;

        var named = UnwrapNamedType(receiverType);
        if (named == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                "Cannot determine the target type for the method from the call-site receiver.");
        }

        return new TargetResolution(named, IsSameType(named, enclosingType), ReceiverIsTypeName: false);
    }

    internal static INamedTypeSymbol? UnwrapNamedType(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol named && named.TypeKind != TypeKind.Error)
            return named;

        return null;
    }

    internal static void ValidateTypeCanHostMethod(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for generate_method_stub.");
        }
    }

    private async Task<TypeDeclarationSyntax> GetEditableTypeDeclarationAsync(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        if (type.Locations.Length == 0 || type.Locations.All(l => !l.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Target type '{type.Name}' is in an external assembly.");
        }

        var syntaxRef = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Target type '{type.Name}' is in an external assembly.");
        }

        if (await syntaxRef.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax syntax)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Target type '{type.Name}' is not editable.");
        }

        return syntax;
    }

    internal static IReadOnlyList<InferredParameter> InferParameters(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<InferredParameter>();
        var index = 0;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            index++;
            var typeName = InferArgumentType(argument.Expression, semanticModel);
            var name = InferParameterName(argument, index, usedNames);
            parameters.Add(new InferredParameter(name, typeName));
        }

        return parameters;
    }

    internal static string InferArgumentType(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        var info = semanticModel.GetTypeInfo(expression);
        var type = info.Type ?? info.ConvertedType;
        if (type != null && type.TypeKind != TypeKind.Error && type.SpecialType != SpecialType.System_Void)
            return type.ToDisplayString();

        return InferLiteralFallback(expression);
    }

    internal static string InferLiteralFallback(ExpressionSyntax expression)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
                || literal.IsKind(SyntaxKind.Utf8StringLiteralExpression) => "string",
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.CharacterLiteralExpression) => "char",
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression)
                || literal.IsKind(SyntaxKind.FalseLiteralExpression) => "bool",
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression) =>
                InferNumericType(literal.Token.Value),
            _ => "object"
        };
    }

    private static string InferNumericType(object? value) => value switch
    {
        float => "float",
        double => "double",
        decimal => "decimal",
        long => "long",
        ulong => "ulong",
        uint => "uint",
        _ => "int"
    };

    internal static string InferParameterName(ArgumentSyntax argument, int index, HashSet<string> usedNames)
    {
        string candidate;
        if (argument.NameColon != null)
        {
            candidate = argument.NameColon.Name.Identifier.ValueText;
        }
        else if (Unwrap(argument.Expression) is IdentifierNameSyntax identifier
                 && IsValidIdentifier(identifier.Identifier.ValueText))
        {
            candidate = identifier.Identifier.ValueText;
        }
        else
        {
            candidate = $"arg{index}";
        }

        var name = candidate;
        var suffix = 2;
        while (!usedNames.Add(name))
        {
            name = $"{candidate}{suffix}";
            suffix++;
        }

        return name;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax paren:
                    expression = paren.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                default:
                    return expression;
            }
        }
    }

    internal static void ValidateNoCompatibleMethod(
        INamedTypeSymbol typeSymbol,
        string methodName,
        IReadOnlyList<InferredParameter> parameters)
    {
        foreach (var existing in typeSymbol.GetMembers(methodName).OfType<IMethodSymbol>())
        {
            if (existing.MethodKind != MethodKind.Ordinary || existing.IsImplicitlyDeclared)
                continue;

            if (existing.Parameters.Length != parameters.Count)
                continue;

            var matches = true;
            for (var i = 0; i < parameters.Count; i++)
            {
                if (!ParameterTypeMatches(parameters[i].TypeName, existing.Parameters[i].Type))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                throw new RefactoringException(
                    ErrorCodes.NameCollision,
                    $"Method '{methodName}' with a compatible signature already exists on '{typeSymbol.Name}'.");
            }
        }
    }

    internal static bool ParameterTypeMatches(string requestedType, ITypeSymbol existingType)
    {
        requestedType = requestedType.Trim();
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            existingType.ToDisplayString(),
            existingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "", StringComparison.Ordinal),
            existingType.Name
        };

        if (TryGetSpecialTypeAlias(existingType.SpecialType, out var alias))
            candidates.Add(alias);

        return candidates.Contains(requestedType);
    }

    internal static string InferReturnType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        GenerateMethodStubParams @params,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(@params.ReturnType))
            return @params.ReturnType.Trim();

        var usage = (SyntaxNode)invocation;
        if (invocation.Parent is AwaitExpressionSyntax awaitExpr)
            usage = awaitExpr;

        switch (usage.Parent)
        {
            case ExpressionStatementSyntax:
                return "void";

            case EqualsValueClauseSyntax equals when equals.Parent is VariableDeclaratorSyntax declarator
                && declarator.Parent is VariableDeclarationSyntax declaration:
                if (declaration.Type.IsVar)
                {
                    throw new RefactoringException(
                        ErrorCodes.CannotInferReturnType,
                        "Cannot infer return type; provide explicit returnType.");
                }

                return declaration.Type.ToString().Trim();

            case AssignmentExpressionSyntax assignment:
                {
                    var leftType = semanticModel.GetTypeInfo(assignment.Left, cancellationToken).Type;
                    if (IsUsableType(leftType))
                        return leftType!.ToDisplayString();
                    break;
                }

            case ReturnStatementSyntax:
                {
                    var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    if (method != null && !method.ReturnType.IsMissing)
                        return method.ReturnType.ToString().Trim();
                    break;
                }
        }

        var converted = semanticModel.GetTypeInfo(usage, cancellationToken).ConvertedType;
        if (IsUsableType(converted))
            return converted!.ToDisplayString();

        throw new RefactoringException(
            ErrorCodes.CannotInferReturnType,
            "Cannot infer return type; provide explicit returnType.");
    }

    internal static bool IsAwaited(InvocationExpressionSyntax invocation) =>
        invocation.Parent is AwaitExpressionSyntax;

    internal static string ToAsyncReturnType(string returnType)
    {
        var trimmed = returnType.Trim();
        if (trimmed.Equals("void", StringComparison.Ordinal))
            return "Task";

        if (IsTaskLike(trimmed))
            return trimmed;

        return $"Task<{trimmed}>";
    }

    private static bool IsTaskLike(string typeName)
    {
        return typeName.Equals("Task", StringComparison.Ordinal)
               || typeName.Equals("ValueTask", StringComparison.Ordinal)
               || typeName.StartsWith("Task<", StringComparison.Ordinal)
               || typeName.StartsWith("ValueTask<", StringComparison.Ordinal)
               || typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal);
    }

    internal static bool ShouldBeStatic(
        TargetResolution target,
        INamedTypeSymbol enclosingType,
        InvocationExpressionSyntax invocation)
    {
        if (target.Type.IsStatic || target.ReceiverIsTypeName)
            return true;

        if (target.SameTypeAsCaller && IsInStaticContext(invocation, enclosingType))
            return true;

        return false;
    }

    private static bool IsInStaticContext(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var member = node.Ancestors().FirstOrDefault(n =>
            n is MethodDeclarationSyntax or LocalFunctionStatementSyntax
                or PropertyDeclarationSyntax or AccessorDeclarationSyntax
                or ConstructorDeclarationSyntax);

        return member switch
        {
            MethodDeclarationSyntax method => method.Modifiers.Any(SyntaxKind.StaticKeyword)
                || semanticModel.GetDeclaredSymbol(method, cancellationToken)?.IsStatic == true,
            LocalFunctionStatementSyntax local => local.Modifiers.Any(SyntaxKind.StaticKeyword),
            PropertyDeclarationSyntax property => property.Modifiers.Any(SyntaxKind.StaticKeyword),
            ConstructorDeclarationSyntax => false,
            _ => false
        };
    }

    private static bool IsInStaticContext(SyntaxNode node, INamedTypeSymbol enclosingType)
    {
        if (enclosingType.IsStatic)
            return true;

        var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method?.Modifiers.Any(SyntaxKind.StaticKeyword) == true;
    }

    internal static string ResolveVisibility(GenerateMethodStubParams @params, bool sameTypeAsCaller)
    {
        if (!string.IsNullOrWhiteSpace(@params.Visibility))
            return @params.Visibility.Trim();

        return sameTypeAsCaller ? "private" : "public";
    }

    /// <summary>
    /// Creates a method stub with a <c>throw new NotImplementedException();</c> body.
    /// </summary>
    internal static MethodDeclarationSyntax CreateMethodStub(
        string name,
        string returnType,
        IReadOnlyList<InferredParameter> parameters,
        string visibility,
        bool isStatic,
        bool isAsync)
    {
        var parameterList = SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(parameters.Select(p =>
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                    .WithType(SyntaxFactory.ParseTypeName(p.TypeName).WithTrailingTrivia(SyntaxFactory.Space)))));

        var modifiers = new List<SyntaxToken>(ParseVisibilityTokens(visibility));
        if (isStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        if (isAsync)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.ParseTypeName(returnType).WithTrailingTrivia(SyntaxFactory.Space),
                name)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(parameterList)
            .WithBody(CreateThrowNotImplementedBody())
            .NormalizeWhitespace();
    }

    internal static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.IdentifierName("NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }

    private static TypeDeclarationSyntax InsertMethod(
        TypeDeclarationSyntax typeDeclaration,
        MethodDeclarationSyntax method)
    {
        var members = typeDeclaration.Members.ToList();
        members.Add(
            method
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        Document targetDocument,
        string typeName,
        string methodName,
        MethodDeclarationSyntax method)
    {
        var afterSnippet = method.NormalizeWhitespace().ToFullString();
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetDocument.FilePath ?? typeName,
                ChangeType = ChangeKind.Modify,
                Description = $"Generate method stub '{methodName}' on {typeName}",
                BeforeSnippet = $"// Type '{typeName}' (no method '{methodName}')",
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
            tokens.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

        return tokens.Select(t => t.WithTrailingTrivia(SyntaxFactory.Space));
    }

    private static SyntaxToken ParseVisibilityKeyword(string keyword) => keyword.ToLowerInvariant() switch
    {
        "public" => SyntaxFactory.Token(SyntaxKind.PublicKeyword),
        "private" => SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
        "protected" => SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
        "internal" => SyntaxFactory.Token(SyntaxKind.InternalKeyword),
        _ => SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
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

    internal static bool IsValidTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var parsed = SyntaxFactory.ParseTypeName(typeName.Trim());
        return parsed is not IdentifierNameSyntax { IsMissing: true }
               && !parsed.ContainsDiagnostics;
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
            SpecialType.System_IntPtr => "nint",
            SpecialType.System_UIntPtr => "nuint",
            SpecialType.System_String => "string",
            SpecialType.System_Object => "object",
            SpecialType.System_Void => "void",
            _ => ""
        };
        return alias.Length > 0;
    }

    private static bool IsUsableType(ITypeSymbol? type) =>
        type != null && type.TypeKind != TypeKind.Error && type.SpecialType != SpecialType.System_Void;

    private static bool IsSameType(INamedTypeSymbol left, INamedTypeSymbol right) =>
        SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

    internal readonly record struct TargetResolution(
        INamedTypeSymbol Type,
        bool SameTypeAsCaller,
        bool ReceiverIsTypeName);

    internal readonly record struct InferredParameter(string Name, string TypeName);
}

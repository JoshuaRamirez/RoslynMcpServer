using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates a method declaration from an undefined call site, inferring the
/// signature from usage. When <see cref="GenerateMethodStubParams.ThrowNotImplemented"/>
/// is true (the default), the placeholder body is
/// <c>throw new global::System.NotImplementedException();</c>.
/// When false, the body uses
/// <see cref="SyntaxGenerationHelper.CreateDefaultReturnBody"/> (empty block
/// for <c>void</c>; <c>return null;</c> for reference types;
/// <c>return default(T);</c> for value types and type parameters), except
/// <c>ref</c> / <c>ref readonly</c> returns which still throw (a default
/// return is not a valid ref return). Async <c>Task</c> / <c>Task&lt;T&gt;</c>
/// stubs unwrap the task type so the body stays compilable.
/// Honors <c>replaceExisting</c> to remove a compatible ordinary method
/// (including across partials) before inserting a freshly generated stub.
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

        var invokedName = GetInvokedName(invocation);
        var methodName = ResolveMethodName(@params, invokedName);
        var typeParameters = InferTypeParameters(invokedName);
        var rewriteCallSite = ShouldRewriteCallSite(@params.MethodName, invokedName, methodName);
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
        var existingMethod = ResolveMethodToReplace(
            target.Type, methodName, parameters, typeParameters.Count, @params.ReplaceExisting);
        // A resolved call is the common replaceExisting case only when it
        // binds to the method we are about to remove. A methodName override
        // that matches a *different* existing method must not skip this
        // guard and rewrite a working call.
        ValidateInvocationIsUnresolved(invocation, semanticModel, existingMethod, cancellationToken);

        var returnType = InferReturnType(invocation, semanticModel, @params, cancellationToken);
        var isAsync = @params.GenerateAsync || IsAwaited(invocation);
        if (isAsync)
            returnType = ToAsyncReturnType(returnType);

        var isStatic = ShouldBeStatic(target, invocation, semanticModel, cancellationToken);
        var visibility = ResolveVisibility(@params, target.SameTypeAsCaller);
        var resolvedReturnType = TryResolveReturnType(semanticModel, returnType, position);
        var method = CreateMethodStub(
            methodName,
            returnType,
            parameters,
            visibility,
            isStatic,
            isAsync,
            typeParameters,
            @params.ThrowNotImplemented,
            resolvedReturnType,
            semanticModel.Compilation);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                callSiteDocument,
                targetDocument,
                target.Type.Name,
                methodName,
                method,
                rewriteCallSite,
                invokedName,
                @params.ThrowNotImplemented,
                existingMethod,
                cancellationToken);
        }

        var newSolution = await ApplyChangesAsync(
            callSiteDocument,
            root,
            targetDocument,
            targetDeclaration,
            method,
            rewriteCallSite ? invokedName : null,
            methodName,
            existingMethod,
            target.Type,
            cancellationToken);

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

    internal static void ValidateInvocationIsUnresolved(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
        => ValidateInvocationIsUnresolved(invocation, semanticModel, allowedReplacement: null, cancellationToken);

    /// <summary>
    /// Rejects a call that already binds, unless it binds to
    /// <paramref name="allowedReplacement"/> — the ordinary method
    /// <c>replaceExisting</c> is about to remove.
    /// </summary>
    internal static void ValidateInvocationIsUnresolved(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IMethodSymbol? allowedReplacement,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol;
        if (symbol == null)
            return;

        if (allowedReplacement != null
            && SymbolEqualityComparer.Default.Equals(symbol, allowedReplacement))
        {
            return;
        }

        throw new RefactoringException(
            ErrorCodes.NameCollision,
            $"The call already resolves to '{symbol.ToDisplayString()}'.");
    }

    private static string ResolveMethodName(
        GenerateMethodStubParams @params,
        SimpleNameSyntax? invokedName)
    {
        if (!string.IsNullOrWhiteSpace(@params.MethodName))
            return @params.MethodName;

        if (invokedName == null || string.IsNullOrWhiteSpace(invokedName.Identifier.ValueText))
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                "No method invocation found at the specified location.");
        }

        return invokedName.Identifier.ValueText;
    }

    internal static bool ShouldRewriteCallSite(string? requestedName, SimpleNameSyntax? invokedName, string methodName) =>
        !string.IsNullOrWhiteSpace(requestedName)
        && invokedName != null
        && !string.Equals(invokedName.Identifier.ValueText, methodName, StringComparison.Ordinal);

    internal static IReadOnlyList<string> InferTypeParameters(SimpleNameSyntax? name)
    {
        if (name is not GenericNameSyntax generic || generic.TypeArgumentList.Arguments.Count == 0)
            return Array.Empty<string>();

        var names = new List<string>(generic.TypeArgumentList.Arguments.Count);
        for (var i = 0; i < generic.TypeArgumentList.Arguments.Count; i++)
            names.Add(i == 0 ? "T" : $"T{i + 1}");

        return names;
    }

    internal static SimpleNameSyntax RenameInvokedName(SimpleNameSyntax current, string newName) =>
        current.WithIdentifier(SyntaxFactory.Identifier(newName).WithTriviaFrom(current.Identifier));

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
            parameters.Add(new InferredParameter(name, typeName, InferRefKind(argument)));
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
        else if (Unwrap(argument.Expression) is DeclarationExpressionSyntax declaration
                 && declaration.Designation is SingleVariableDesignationSyntax single
                 && IsValidIdentifier(single.Identifier.ValueText))
        {
            candidate = single.Identifier.ValueText;
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

    internal static RefKind InferRefKind(ArgumentSyntax argument) => argument.RefKindKeyword.Kind() switch
    {
        SyntaxKind.RefKeyword => RefKind.Ref,
        SyntaxKind.OutKeyword => RefKind.Out,
        SyntaxKind.InKeyword => RefKind.In,
        _ => RefKind.None
    };

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

    /// <summary>
    /// Rejects a compatible-signature clash when <paramref name="replaceExisting"/>
    /// is false, or returns the single existing ordinary method that
    /// <c>replaceExisting</c> will remove. Constructors, operators, local
    /// functions, explicit interface implementations, accessors, and other
    /// non-ordinary methods are never replaced. Two compatible ordinary
    /// methods with no single target fail before any write.
    /// </summary>
    internal static IMethodSymbol? ResolveMethodToReplace(
        INamedTypeSymbol typeSymbol,
        string methodName,
        IReadOnlyList<InferredParameter> parameters,
        int typeParameterCount,
        bool replaceExisting)
    {
        var matches = FindCompatibleOrdinaryMethods(
            typeSymbol, methodName, parameters, typeParameterCount);

        if (matches.Count >= 2)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Multiple methods named '{methodName}' with a compatible signature exist on '{typeSymbol.Name}'; replaceExisting cannot choose a target.");
        }

        if (matches.Count == 1)
        {
            if (replaceExisting && HasMethodDeclaration(matches[0]))
                return matches[0];

            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Method '{methodName}' with a compatible signature already exists on '{typeSymbol.Name}'.");
        }

        return null;
    }

    /// <summary>
    /// Finds ordinary methods on <paramref name="typeSymbol"/> that match
    /// today's <see cref="ValidateNoCompatibleMethod"/> rules (name,
    /// type-parameter arity, parameter count, <see cref="ParameterTypeMatches"/>)
    /// plus <see cref="RefKind"/> so <c>ref</c>/<c>out</c>/<c>in</c>
    /// overloads are not wrongly replaced.
    /// </summary>
    internal static List<IMethodSymbol> FindCompatibleOrdinaryMethods(
        INamedTypeSymbol typeSymbol,
        string methodName,
        IReadOnlyList<InferredParameter> parameters,
        int typeParameterCount)
    {
        var matches = new List<IMethodSymbol>();
        foreach (var existing in typeSymbol.GetMembers(methodName).OfType<IMethodSymbol>())
        {
            if (!IsReplaceableOrdinaryMethod(existing))
                continue;

            if (existing.TypeParameters.Length != typeParameterCount)
                continue;

            if (existing.Parameters.Length != parameters.Count)
                continue;

            var matchesSignature = true;
            for (var i = 0; i < parameters.Count; i++)
            {
                if (existing.Parameters[i].RefKind != parameters[i].RefKind
                    || !ParameterTypeMatches(parameters[i].TypeName, existing.Parameters[i].Type))
                {
                    matchesSignature = false;
                    break;
                }
            }

            if (matchesSignature)
                matches.Add(existing);
        }

        return matches;
    }

    /// <summary>
    /// True for a user-declared ordinary method that replaceExisting may
    /// remove. Constructors, operators, local functions, accessors,
    /// explicit interface implementations, and implicit members are
    /// excluded.
    /// </summary>
    internal static bool IsReplaceableOrdinaryMethod(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsImplicitlyDeclared)
            return false;

        if (method.ExplicitInterfaceImplementations.Length > 0)
            return false;

        return true;
    }

    internal static bool HasMethodDeclaration(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is MethodDeclarationSyntax);

    /// <summary>
    /// Fail-on-clash used when <c>replaceExisting</c> is false / omitted.
    /// </summary>
    internal static void ValidateNoCompatibleMethod(
        INamedTypeSymbol typeSymbol,
        string methodName,
        IReadOnlyList<InferredParameter> parameters,
        int typeParameterCount)
    {
        ResolveMethodToReplace(typeSymbol, methodName, parameters, typeParameterCount, replaceExisting: false);
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
        else if (invocation.Parent is RefExpressionSyntax refExpr)
            usage = refExpr;

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
        SyntaxNode invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (target.Type.IsStatic || target.ReceiverIsTypeName)
            return true;

        return target.SameTypeAsCaller && IsInStaticContext(invocation, semanticModel, cancellationToken);
    }

    internal static bool IsInStaticContext(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var enclosingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (enclosingType?.Modifiers.Any(SyntaxKind.StaticKeyword) == true)
            return true;

        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Modifiers.Any(SyntaxKind.StaticKeyword)
                        || semanticModel.GetDeclaredSymbol(method, cancellationToken)?.IsStatic == true;
                case LocalFunctionStatementSyntax local:
                    return local.Modifiers.Any(SyntaxKind.StaticKeyword);
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Modifiers.Any(SyntaxKind.StaticKeyword);
                case PropertyDeclarationSyntax property:
                    return property.Modifiers.Any(SyntaxKind.StaticKeyword);
                case EventDeclarationSyntax evt:
                    return evt.Modifiers.Any(SyntaxKind.StaticKeyword);
                case AccessorDeclarationSyntax accessor:
                    return IsContainingMemberStatic(accessor);
            }
        }

        return false;
    }

    private static bool IsContainingMemberStatic(AccessorDeclarationSyntax accessor)
    {
        return accessor.Parent?.Parent switch
        {
            PropertyDeclarationSyntax property => property.Modifiers.Any(SyntaxKind.StaticKeyword),
            EventDeclarationSyntax evt => evt.Modifiers.Any(SyntaxKind.StaticKeyword),
            IndexerDeclarationSyntax => false,
            _ => false
        };
    }

    internal static string ResolveVisibility(GenerateMethodStubParams @params, bool sameTypeAsCaller)
    {
        if (!string.IsNullOrWhiteSpace(@params.Visibility))
            return @params.Visibility.Trim();

        return sameTypeAsCaller ? "private" : "public";
    }

    /// <summary>
    /// Creates a method stub. When <paramref name="throwNotImplemented"/> is
    /// true, the body is
    /// <c>throw new global::System.NotImplementedException();</c>;
    /// otherwise it uses <see cref="SyntaxGenerationHelper.CreateDefaultReturnBody"/>
    /// (or the same shape when the return type cannot be bound).
    /// <c>ref</c> / <c>ref readonly</c> methods always throw — a default
    /// return is not a valid ref return (CS8156). Async <c>Task</c> /
    /// <c>Task&lt;T&gt;</c> stubs unwrap the task so the body compiles
    /// (empty block / return-default of the inner type; no dummy storage).
    /// </summary>
    internal static MethodDeclarationSyntax CreateMethodStub(
        string name,
        string returnType,
        IReadOnlyList<InferredParameter> parameters,
        string visibility,
        bool isStatic,
        bool isAsync,
        IReadOnlyList<string>? typeParameters = null,
        bool throwNotImplemented = true,
        ITypeSymbol? resolvedReturnType = null,
        Compilation? compilation = null)
    {
        var parameterList = SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(parameters.Select(CreateParameter)));

        var modifiers = new List<SyntaxToken>(ParseVisibilityTokens(visibility));
        if (isStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        if (isAsync)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var body = RequiresThrowBody(returnType, throwNotImplemented)
            ? CreateThrowNotImplementedBody()
            : CreateNonThrowingBody(returnType, isAsync, resolvedReturnType, typeParameters, compilation);

        var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.ParseTypeName(returnType).WithTrailingTrivia(SyntaxFactory.Space),
                name)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(parameterList)
            .WithBody(body);

        if (typeParameters is { Count: > 0 })
        {
            method = method.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(
                    SyntaxFactory.SeparatedList(typeParameters.Select(SyntaxFactory.TypeParameter))));
        }

        return method.NormalizeWhitespace();
    }

    /// <summary>
    /// True when the stub must throw: the flag is on, or the inferred /
    /// requested return is <c>ref</c> / <c>ref readonly</c> (CS8156).
    /// </summary>
    internal static bool RequiresThrowBody(string returnType, bool throwNotImplemented)
        => throwNotImplemented || IsRefReturn(returnType);

    /// <summary>
    /// True when <paramref name="returnType"/> is a <c>ref</c> or
    /// <c>ref readonly</c> return (a default return is not valid).
    /// </summary>
    internal static bool IsRefReturn(string returnType)
    {
        var trimmed = returnType.Trim();
        return trimmed.StartsWith("ref ", StringComparison.Ordinal);
    }

    internal static ITypeSymbol? TryResolveReturnType(
        SemanticModel semanticModel,
        string returnType,
        int position)
    {
        var stripped = StripRefModifiers(returnType);
        if (stripped.Equals("void", StringComparison.Ordinal))
            return semanticModel.Compilation.GetSpecialType(SpecialType.System_Void);

        var typeSyntax = SyntaxFactory.ParseTypeName(stripped);
        var info = semanticModel.GetSpeculativeTypeInfo(
            position,
            typeSyntax,
            SpeculativeBindingOption.BindAsTypeOrNamespace);
        return IsUsableType(info.Type) ? info.Type : null;
    }

    internal static string StripRefModifiers(string returnType)
    {
        var trimmed = returnType.Trim();
        if (trimmed.StartsWith("ref readonly ", StringComparison.Ordinal))
            return trimmed["ref readonly ".Length..].Trim();
        if (trimmed.StartsWith("ref ", StringComparison.Ordinal))
            return trimmed["ref ".Length..].Trim();
        return trimmed;
    }

    private static BlockSyntax CreateNonThrowingBody(
        string returnType,
        bool isAsync,
        ITypeSymbol? resolvedReturnType,
        IReadOnlyList<string>? typeParameters,
        Compilation? compilation)
    {
        var bodyType = isAsync
            ? UnwrapTaskLike(resolvedReturnType, compilation)
            : resolvedReturnType;

        if (bodyType != null)
            return SyntaxGenerationHelper.CreateDefaultReturnBody(bodyType);

        var bodyTypeName = isAsync
            ? UnwrapAsyncReturnTypeName(returnType)
            : StripRefModifiers(returnType);
        return CreateDefaultReturnBodyFromTypeName(bodyTypeName, typeParameters);
    }

    /// <summary>
    /// Unwraps <c>Task</c> / <c>ValueTask</c> to <c>void</c> and
    /// <c>Task&lt;T&gt;</c> / <c>ValueTask&lt;T&gt;</c> to <c>T</c> so
    /// <see cref="SyntaxGenerationHelper.CreateDefaultReturnBody"/> is applied
    /// to the type an <c>async</c> method actually returns.
    /// </summary>
    internal static ITypeSymbol? UnwrapTaskLike(ITypeSymbol? type, Compilation? compilation)
    {
        if (type is not INamedTypeSymbol named || !IsTaskLikeSymbol(named))
            return type;

        if (named.TypeArguments.Length == 1)
            return named.TypeArguments[0];

        return compilation?.GetSpecialType(SpecialType.System_Void);
    }

    internal static bool IsTaskLikeSymbol(INamedTypeSymbol type)
    {
        if (type.Name is not ("Task" or "ValueTask"))
            return false;

        return type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    }

    /// <summary>
    /// String-level Task unwrap used when the return type could not be bound.
    /// </summary>
    internal static string UnwrapAsyncReturnTypeName(string returnType)
    {
        var trimmed = StripRefModifiers(returnType);
        if (IsNonGenericTaskName(trimmed))
            return "void";

        if (TryUnwrapGeneric(trimmed, "Task", out var inner)
            || TryUnwrapGeneric(trimmed, "ValueTask", out inner)
            || TryUnwrapGeneric(trimmed, "System.Threading.Tasks.Task", out inner)
            || TryUnwrapGeneric(trimmed, "System.Threading.Tasks.ValueTask", out inner))
        {
            return inner;
        }

        return trimmed;
    }

    private static bool IsNonGenericTaskName(string typeName) =>
        typeName.Equals("Task", StringComparison.Ordinal)
        || typeName.Equals("ValueTask", StringComparison.Ordinal)
        || typeName.Equals("System.Threading.Tasks.Task", StringComparison.Ordinal)
        || typeName.Equals("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);

    private static bool TryUnwrapGeneric(string typeName, string genericName, out string inner)
    {
        var prefix = genericName + "<";
        if (typeName.StartsWith(prefix, StringComparison.Ordinal)
            && typeName.EndsWith(">", StringComparison.Ordinal)
            && typeName.Length > prefix.Length + 1)
        {
            inner = typeName.Substring(prefix.Length, typeName.Length - prefix.Length - 1);
            return true;
        }

        inner = "";
        return false;
    }

    /// <summary>
    /// Fallback when the return type cannot be bound to an
    /// <see cref="ITypeSymbol"/> — same shapes as
    /// <see cref="SyntaxGenerationHelper.CreateDefaultReturnBody"/>.
    /// </summary>
    internal static BlockSyntax CreateDefaultReturnBodyFromTypeName(
        string returnType,
        IReadOnlyList<string>? typeParameters)
    {
        var trimmed = returnType.Trim();
        if (trimmed.Equals("void", StringComparison.Ordinal))
            return SyntaxFactory.Block();

        if (IsKnownValueTypeName(trimmed)
            || IsGeneratedTypeParameter(trimmed, typeParameters))
        {
            return SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(
                    SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(trimmed))));
        }

        if (IsKnownReferenceTypeName(trimmed))
        {
            return SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        }

        // Unresolved user types: default(T) compiles for both structs and classes.
        return SyntaxFactory.Block(
            SyntaxFactory.ReturnStatement(
                SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(trimmed))));
    }

    private static bool IsGeneratedTypeParameter(string typeName, IReadOnlyList<string>? typeParameters) =>
        typeParameters != null
        && typeParameters.Any(tp => tp.Equals(typeName, StringComparison.Ordinal));

    private static bool IsKnownValueTypeName(string typeName) =>
        typeName is "bool" or "byte" or "sbyte" or "short" or "ushort"
            or "int" or "uint" or "long" or "ulong"
            or "float" or "double" or "decimal"
            or "char" or "nint" or "nuint";

    private static bool IsKnownReferenceTypeName(string typeName) =>
        typeName is "string" or "object" or "dynamic"
        || typeName.EndsWith("[]", StringComparison.Ordinal);

    private static ParameterSyntax CreateParameter(InferredParameter parameter)
    {
        var syntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name))
            .WithType(SyntaxFactory.ParseTypeName(parameter.TypeName).WithTrailingTrivia(SyntaxFactory.Space));

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

    internal static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("global::System.NotImplementedException"))
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

    /// <summary>
    /// Removes the matched method declaration from every partial that holds
    /// it. Match by span/kind, not SyntaxNode reference — same seam as
    /// generate_property replaceExisting. Uses
    /// <see cref="SyntaxRemoveOptions.KeepExteriorTrivia"/> and
    /// <see cref="SyntaxRemoveOptions.KeepDirectives"/> so a leading
    /// <c>#if</c> / <c>#region</c> on the removed method does not orphan a
    /// following <c>#endif</c> / <c>#endregion</c>.
    /// </summary>
    private static async Task<Solution> RemoveExistingMethodsAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        // Key by DocumentId, not SyntaxTree identity — a prior in-memory
        // annotation on the call-site document replaces that tree.
        var membersByDocumentAndPart = new Dictionary<DocumentId, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            if (syntax is not MethodDeclarationSyntax)
                continue;
            if (syntax.Parent is not TypeDeclarationSyntax part)
                continue;

            var document = GetDocumentForTree(solution, syntax.SyntaxTree)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate a declaring document for type '{typeSymbol.Name}'.");

            if (!membersByDocumentAndPart.TryGetValue(document.Id, out var byPart))
            {
                byPart = new Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>();
                membersByDocumentAndPart[document.Id] = byPart;
            }

            if (!byPart.TryGetValue(part.SpanStart, out var keys))
            {
                keys = new HashSet<(int Start, int End, SyntaxKind Kind)>();
                byPart[part.SpanStart] = keys;
            }

            keys.Add((syntax.SpanStart, syntax.Span.End, syntax.Kind()));
        }

        foreach (var (documentId, byPart) in membersByDocumentAndPart)
        {
            var document = solution.GetDocument(documentId)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate a declaring document for type '{typeSymbol.Name}'.");
            var treeRoot = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var toRemove = new List<MemberDeclarationSyntax>();
            foreach (var part in treeRoot.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!TypeNameMatches(part, typeSymbol.Name))
                    continue;
                if (!byPart.TryGetValue(part.SpanStart, out var keys) || keys.Count == 0)
                    continue;

                foreach (var member in part.Members)
                {
                    if (keys.Contains((member.SpanStart, member.Span.End, member.Kind())))
                        toRemove.Add(member);
                }
            }

            if (toRemove.Count == 0)
                continue;

            // KeepDirectives / KeepExteriorTrivia so a leading #if / #region on
            // the removed method does not orphan a following #endif / #endregion.
            var newRoot = treeRoot.RemoveNodes(
                    toRemove,
                    SyntaxRemoveOptions.KeepExteriorTrivia | SyntaxRemoveOptions.KeepDirectives)
                ?? treeRoot;
            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    /// <summary>
    /// Resolves a document after an in-memory <c>WithSyntaxRoot</c> may have
    /// replaced the tree identity. Fall back to file path so a call-site
    /// annotation does not make the declaring document unfindable.
    /// </summary>
    internal static Document? GetDocumentForTree(Solution solution, SyntaxTree tree)
    {
        var document = solution.GetDocument(tree);
        if (document != null)
            return document;

        if (string.IsNullOrWhiteSpace(tree.FilePath))
            return null;

        var ids = solution.GetDocumentIdsWithFilePath(tree.FilePath);
        return ids.Length > 0 ? solution.GetDocument(ids[0]) : null;
    }

    /// <summary>
    /// Matches a type declaration to a symbol name. Escaped identifiers
    /// such as <c>class @class</c> have <see cref="SyntaxToken.Text"/>
    /// <c>@class</c> and <see cref="SyntaxToken.ValueText"/> <c>class</c>
    /// (the latter equals <see cref="ISymbol.Name"/>).
    /// </summary>
    internal static bool TypeNameMatches(TypeDeclarationSyntax type, string typeName) =>
        string.Equals(type.Identifier.ValueText, typeName, StringComparison.Ordinal)
        || string.Equals(type.Identifier.Text, typeName, StringComparison.Ordinal);

    private static TypeDeclarationSyntax? FindTypeDeclaration(SyntaxNode root, string typeName, int preferredSpanStart)
    {
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => TypeNameMatches(t, typeName))
            .ToList();
        return matches.FirstOrDefault(t => t.SpanStart == preferredSpanStart) ?? matches.FirstOrDefault();
    }

    private async Task<Solution> ApplyChangesAsync(
        Document callSiteDocument,
        SyntaxNode callSiteRoot,
        Document targetDocument,
        TypeDeclarationSyntax targetDeclaration,
        MethodDeclarationSyntax method,
        SimpleNameSyntax? invokedNameToRewrite,
        string methodName,
        IMethodSymbol? existingToReplace,
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken)
    {
        var solution = callSiteDocument.Project.Solution;
        var callAnnotation = new SyntaxAnnotation("generate-method-stub-call");

        // Annotate the call-site name before any same-file remove so a
        // method above the invocation cannot shift the rewrite target.
        if (invokedNameToRewrite != null)
        {
            var annotated = invokedNameToRewrite.WithAdditionalAnnotations(callAnnotation);
            callSiteRoot = callSiteRoot.ReplaceNode(invokedNameToRewrite, annotated);
            callSiteDocument = callSiteDocument.WithSyntaxRoot(callSiteRoot);
            solution = callSiteDocument.Project.Solution;
            invokedNameToRewrite = (SimpleNameSyntax)callSiteRoot.GetAnnotatedNodes(callAnnotation).Single();

            if (targetDocument.Id == callSiteDocument.Id)
            {
                targetDocument = callSiteDocument;
                targetDeclaration = (TypeDeclarationSyntax)callSiteRoot.FindNode(targetDeclaration.Span);
            }
            else
            {
                targetDocument = solution.GetDocument(targetDocument.Id)
                    ?? throw new RefactoringException(
                        ErrorCodes.RoslynError,
                        "Could not locate the target document.");
            }
        }

        if (existingToReplace != null)
        {
            solution = await RemoveExistingMethodsAcrossPartialsAsync(
                solution, typeSymbol, existingToReplace, cancellationToken);

            callSiteDocument = solution.GetDocument(callSiteDocument.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    "Could not locate the call-site document.");
            callSiteRoot = await callSiteDocument.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            targetDocument = solution.GetDocument(targetDocument.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{typeSymbol.Name}'.");
            var refreshedTargetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse target file.");
            targetDeclaration = FindTypeDeclaration(
                    refreshedTargetRoot, typeSymbol.Name, targetDeclaration.SpanStart)
                ?? throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"Could not relocate type '{typeSymbol.Name}' after removing the existing method.");

            if (invokedNameToRewrite != null)
            {
                invokedNameToRewrite = callSiteRoot.GetAnnotatedNodes(callAnnotation)
                    .OfType<SimpleNameSyntax>()
                    .FirstOrDefault();
                if (invokedNameToRewrite == null)
                {
                    throw new RefactoringException(
                        ErrorCodes.MethodNotFound,
                        "Could not rewrite the call site to the overridden method name.");
                }
            }
        }

        if (targetDocument.Id == callSiteDocument.Id)
        {
            var targetInCallSite = (TypeDeclarationSyntax)callSiteRoot.FindNode(targetDeclaration.Span);
            SyntaxNode newRoot;
            if (invokedNameToRewrite != null && targetInCallSite.FullSpan.Contains(invokedNameToRewrite.Span))
            {
                var renamed = RenameInvokedName(invokedNameToRewrite, methodName);
                var typeWithRename = (TypeDeclarationSyntax)targetInCallSite.ReplaceNode(invokedNameToRewrite, renamed);
                newRoot = callSiteRoot.ReplaceNode(targetInCallSite, InsertMethod(typeWithRename, method));
            }
            else if (invokedNameToRewrite != null)
            {
                var renamed = RenameInvokedName(invokedNameToRewrite, methodName);
                newRoot = callSiteRoot.ReplaceNodes(
                    new SyntaxNode[] { invokedNameToRewrite, targetInCallSite },
                    (original, _) => original == invokedNameToRewrite
                        ? renamed
                        : InsertMethod(targetInCallSite, method));
            }
            else
            {
                newRoot = callSiteRoot.ReplaceNode(targetInCallSite, InsertMethod(targetInCallSite, method));
            }

            return callSiteDocument.WithSyntaxRoot(newRoot).Project.Solution;
        }

        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse target file.");
        var currentDecl = (TypeDeclarationSyntax)targetRoot.FindNode(targetDeclaration.Span);
        var newTargetRoot = targetRoot.ReplaceNode(currentDecl, InsertMethod(currentDecl, method));
        solution = targetDocument.WithSyntaxRoot(newTargetRoot).Project.Solution;

        if (invokedNameToRewrite == null)
            return solution;

        var updatedCallSite = solution.GetDocument(callSiteDocument.Id)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not locate the call-site document.");
        var updatedRoot = await updatedCallSite.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        if (updatedRoot.FindNode(invokedNameToRewrite.Span) is not SimpleNameSyntax currentName)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                "Could not rewrite the call site to the overridden method name.");
        }

        var rewrittenRoot = updatedRoot.ReplaceNode(currentName, RenameInvokedName(currentName, methodName));
        return updatedCallSite.WithSyntaxRoot(rewrittenRoot).Project.Solution;
    }

    /// <summary>
    /// Creates a preview result with the generated method stub.
    /// When replacing a method that lives in another partial, also includes
    /// a Modify pending change per distinct declaring file with that method
    /// as <c>BeforeSnippet</c> — same as generate_property replaceExisting
    /// preview.
    /// </summary>
    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        Document callSiteDocument,
        Document targetDocument,
        string typeName,
        string methodName,
        MethodDeclarationSyntax method,
        bool rewriteCallSite,
        SimpleNameSyntax? invokedName,
        bool throwNotImplemented,
        IMethodSymbol? existingMethod,
        CancellationToken cancellationToken)
    {
        var replacing = existingMethod != null;
        var verb = replacing ? "Replace" : "Generate";
        var afterSnippet = method.NormalizeWhitespace().ToFullString();
        // Match the body that will actually be emitted: a false flag still
        // throws for ref / ref readonly returns (CS8156).
        var throwNote = RequiresThrowBody(method.ReturnType.ToString(), throwNotImplemented)
            ? "stub will throw NotImplementedException"
            : "stub will not throw";
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetDocument.FilePath ?? typeName,
                ChangeType = ChangeKind.Modify,
                Description = $"{verb} method stub '{methodName}' on {typeName} ({throwNote})",
                BeforeSnippet = replacing
                    ? $"// Type '{typeName}' (replacing existing method '{methodName}')"
                    : $"// Type '{typeName}' (no method '{methodName}')",
                AfterSnippet = afterSnippet
            }
        };

        if (existingMethod != null)
        {
            var sourcePath = PathResolver.NormalizePath(targetDocument.FilePath ?? "");
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in existingMethod.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (syntax is not MethodDeclarationSyntax existingMethodSyntax)
                    continue;

                var declaringDocument = callSiteDocument.Project.Solution.GetDocument(syntax.SyntaxTree);
                var filePath = declaringDocument?.FilePath ?? syntax.SyntaxTree.FilePath;
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                var normalized = PathResolver.NormalizePath(filePath);
                if (!seenFiles.Add(normalized))
                    continue;
                if (string.Equals(normalized, sourcePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                pendingChanges.Add(new PendingChange
                {
                    File = filePath,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Remove existing method '{methodName}' from {typeName}",
                    BeforeSnippet = existingMethodSyntax.NormalizeWhitespace().ToFullString(),
                    AfterSnippet = "// method removed"
                });
            }
        }

        if (rewriteCallSite && invokedName != null)
        {
            pendingChanges.Add(new PendingChange
            {
                File = callSiteDocument.FilePath ?? invokedName.ToString(),
                ChangeType = ChangeKind.Modify,
                Description = $"Rewrite call site '{invokedName.Identifier.ValueText}' to '{methodName}'",
                BeforeSnippet = invokedName.ToString(),
                AfterSnippet = RenameInvokedName(invokedName, methodName).ToString()
            });
        }

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

    internal readonly record struct InferredParameter(string Name, string TypeName, RefKind RefKind);
}

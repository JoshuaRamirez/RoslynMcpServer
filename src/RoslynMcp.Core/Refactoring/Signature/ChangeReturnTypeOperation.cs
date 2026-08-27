using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Changes a method's return type and updates return statements, overrides,
/// and interface implementations when the conversion is safe.
/// </summary>
public sealed class ChangeReturnTypeOperation : RefactoringOperationBase<ChangeReturnTypeParams>
{
    /// <summary>
    /// Creates a new change return type operation.
    /// </summary>
    public ChangeReturnTypeOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ChangeReturnTypeParams @params) => Validate(@params);

    /// <summary>
    /// Validates change-return-type inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(ChangeReturnTypeParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (string.IsNullOrWhiteSpace(@params.NewReturnType))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newReturnType is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidReturnType(@params.NewReturnType))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidReturnType,
                $"'{@params.NewReturnType}' is not a valid C# return type.");
        }

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
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
        ChangeReturnTypeParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var methodDecl = FindMethodDeclaration(root, @params);
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        var newReturnType = ResolveReturnType(semanticModel, methodDecl, @params.NewReturnType)
            ?? throw new RefactoringException(
                ErrorCodes.InvalidReturnType,
                $"'{@params.NewReturnType}' is not a valid C# return type.");

        if (TypesEquivalent(methodSymbol.ReturnType, newReturnType))
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                $"New return type '{@params.NewReturnType}' is the same as the current return type.");
        }

        ValidateNotAsyncOrTaskLike(methodSymbol, newReturnType);

        var contractMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            updateOverrides: true,
            updateImplementations: true,
            cancellationToken);
        ValidateUneditableContracts(contractMethods, newReturnType, semanticModel.Compilation);

        var relatedMethods = (await GetRelatedMethodsAsync(
                methodSymbol,
                @params.UpdateOverrides,
                @params.UpdateImplementations,
                cancellationToken))
            .Where(HasSourceDeclaration)
            .ToList();

        foreach (var related in relatedMethods)
        {
            ValidateNotAsyncOrTaskLike(related, newReturnType);
            ValidateNoOverloadCollision(related, newReturnType);
        }

        var declarationTargets = await CollectDeclarationTargetsAsync(
            relatedMethods,
            newReturnType,
            @params.ConvertReturnStatements,
            cancellationToken);

        foreach (var target in declarationTargets)
            ValidateDocumentIsEditable(target.Document, Context.Workspace);

        await ValidateReferencesAsync(relatedMethods, newReturnType, cancellationToken);

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                cancellationToken);
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
                Name = @params.MethodName,
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            declarationTargets.Sum(t => t.ReturnSpans.Count),
            0);
    }

    internal static bool IsValidReturnType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        var remainder = type.Trim();
        if (remainder == "void")
            return true;

        var parsed = SyntaxFactory.ParseTypeName(type);
        if (parsed.ContainsDiagnostics || parsed.IsMissing)
            return false;

        if (parsed is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return true;
        }

        var written = parsed.ToString();
        if (!string.Equals(written.Trim(), remainder, StringComparison.Ordinal))
        {
            // ParseTypeName is permissive; reject leftovers such as "int int".
            if (remainder.Length > written.Trim().Length)
                return false;
        }

        return parsed is not IdentifierNameSyntax identifier || !identifier.IsMissing;
    }

    internal static ITypeSymbol? ResolveReturnType(
        SemanticModel semanticModel,
        MethodDeclarationSyntax method,
        string newReturnType)
    {
        var trimmed = newReturnType.Trim();
        if (trimmed == "void")
            return semanticModel.Compilation.GetSpecialType(SpecialType.System_Void);

        var parsed = SyntaxFactory.ParseTypeName(newReturnType);
        if (parsed.ContainsDiagnostics || parsed.IsMissing)
            return null;

        if (parsed is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return semanticModel.Compilation.GetSpecialType(SpecialType.System_Void);
        }

        var speculative = semanticModel.GetSpeculativeTypeInfo(
            method.ReturnType.SpanStart,
            parsed,
            SpeculativeBindingOption.BindAsTypeOrNamespace);

        if (speculative.Type != null && speculative.Type.TypeKind != TypeKind.Error)
            return speculative.Type;

        return semanticModel.Compilation.GetTypeByMetadataName(trimmed);
    }

    internal static void ValidateNoOverloadCollision(IMethodSymbol method, ITypeSymbol newReturnType)
    {
        if (method.ContainingType == null)
            return;

        foreach (var candidate in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, method))
                continue;
            if (candidate.Parameters.Length != method.Parameters.Length)
                continue;
            if (candidate.TypeParameters.Length != method.TypeParameters.Length)
                continue;
            if (!ParameterTypesMatch(method, candidate))
                continue;
            if (!TypesEquivalent(candidate.ReturnType, newReturnType))
                continue;

            throw new RefactoringException(
                ErrorCodes.SignatureMatchesOverload,
                $"Changing the return type of '{method.Name}' would match an existing overload.");
        }
    }

    private static bool ParameterTypesMatch(IMethodSymbol method, IMethodSymbol other)
    {
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var left = method.Parameters[i];
            var right = other.Parameters[i];
            if (left.RefKind != right.RefKind)
                return false;
            if (!TypesEquivalent(left.Type, right.Type))
                return false;
        }

        return true;
    }

    internal static bool TypesEquivalent(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left is ITypeParameterSymbol leftTypeParameter &&
            right is ITypeParameterSymbol rightTypeParameter)
        {
            return leftTypeParameter.TypeParameterKind == rightTypeParameter.TypeParameterKind &&
                   leftTypeParameter.Ordinal == rightTypeParameter.Ordinal &&
                   (leftTypeParameter.TypeParameterKind != TypeParameterKind.Type ||
                    SymbolEqualityComparer.Default.Equals(
                        leftTypeParameter.ContainingType,
                        rightTypeParameter.ContainingType));
        }

        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank &&
                   TypesEquivalent(leftArray.ElementType, rightArray.ElementType);
        }

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            if (!SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition))
                return false;
            if (leftNamed.TypeArguments.Length != rightNamed.TypeArguments.Length)
                return false;

            for (var i = 0; i < leftNamed.TypeArguments.Length; i++)
            {
                if (!TypesEquivalent(leftNamed.TypeArguments[i], rightNamed.TypeArguments[i]))
                    return false;
            }

            return true;
        }

        return false;
    }

    internal static MethodDeclarationSyntax FindMethodDeclaration(SyntaxNode root, ChangeReturnTypeParams @params)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == @params.MethodName)
            .ToList();

        if (methods.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{@params.MethodName}' not found.");
        }

        if (methods.Count == 1 && !@params.Line.HasValue && !@params.Column.HasValue)
            return methods[0];

        IEnumerable<MethodDeclarationSyntax> filtered = methods;
        if (@params.Line.HasValue)
        {
            filtered = filtered.Where(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value);
        }

        if (@params.Column.HasValue)
        {
            filtered = filtered.Where(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Character + 1 == @params.Column.Value);
        }

        var matches = filtered.ToList();
        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                @params.Line.HasValue
                    ? $"Method '{@params.MethodName}' not found at line {@params.Line}."
                    : $"Method '{@params.MethodName}' not found.");
        }

        var lines = matches
            .Select(m => m.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
    }

    private async Task<List<IMethodSymbol>> GetRelatedMethodsAsync(
        IMethodSymbol method,
        bool updateOverrides,
        bool updateImplementations,
        CancellationToken cancellationToken)
    {
        var results = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { method };

        if (updateOverrides)
        {
            var current = method;
            while (current.OverriddenMethod != null)
            {
                results.Add(current.OverriddenMethod);
                current = current.OverriddenMethod;
            }

            foreach (var symbol in results.ToList())
            {
                var overrides = await SymbolFinder.FindOverridesAsync(
                    symbol, Context.Solution, cancellationToken: cancellationToken);
                foreach (var ov in overrides.OfType<IMethodSymbol>())
                    results.Add(ov);
            }
        }

        if (updateImplementations)
        {
            foreach (var candidate in results.ToList())
            {
                if (candidate.ContainingType.TypeKind == TypeKind.Interface)
                {
                    var implementations = await SymbolFinder.FindImplementationsAsync(
                        candidate, Context.Solution, cancellationToken: cancellationToken);
                    foreach (var impl in implementations.OfType<IMethodSymbol>())
                        results.Add(impl);
                    continue;
                }

                foreach (var iface in candidate.ContainingType.AllInterfaces)
                {
                    foreach (var ifaceMethod in iface.GetMembers(candidate.Name).OfType<IMethodSymbol>())
                    {
                        var impl = candidate.ContainingType.FindImplementationForInterfaceMember(ifaceMethod);
                        if (impl is not IMethodSymbol implMethod ||
                            !ShareOverrideRoot(implMethod, candidate))
                        {
                            continue;
                        }

                        results.Add(ifaceMethod);
                        var otherImpls = await SymbolFinder.FindImplementationsAsync(
                            ifaceMethod,
                            Context.Solution,
                            cancellationToken: cancellationToken);
                        foreach (var other in otherImpls.OfType<IMethodSymbol>())
                            results.Add(other);
                    }
                }
            }
        }

        return results.ToList();
    }

    internal static void ValidateNotAsyncOrTaskLike(IMethodSymbol method, ITypeSymbol newReturnType)
    {
        if (!method.IsAsync && !IsTaskLike(method.ReturnType) && !IsTaskLike(newReturnType))
            return;

        throw new RefactoringException(
            ErrorCodes.AsyncReturnTypeUnsupported,
            "change_return_type does not support async methods or Task/ValueTask return types.");
    }

    internal static bool IsTaskLike(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var definition = named.OriginalDefinition;
        if (definition.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks")
            return false;

        return definition.Name is "Task" or "ValueTask";
    }

    internal static void ValidateUneditableContracts(
        IEnumerable<IMethodSymbol> methods,
        ITypeSymbol newReturnType,
        Compilation compilation)
    {
        foreach (var method in methods)
        {
            if (HasSourceDeclaration(method))
                continue;

            if (IsCompatibleWithUneditableContract(method.ReturnType, newReturnType, compilation))
                continue;

            throw new RefactoringException(
                ErrorCodes.ReturnTypeIncompatible,
                $"Changing the return type of '{method.Name}' would break an uneditable override or interface contract.");
        }
    }

    internal static bool IsCompatibleWithUneditableContract(
        ITypeSymbol contractReturn,
        ITypeSymbol newReturn,
        Compilation compilation)
    {
        if (TypesEquivalent(contractReturn, newReturn))
            return true;

        if (!newReturn.IsReferenceType || !contractReturn.IsReferenceType)
            return false;

        var conversion = compilation.ClassifyConversion(newReturn, contractReturn);
        return conversion.Exists && conversion.IsImplicit && (conversion.IsIdentity || conversion.IsReference);
    }

    private async Task<List<DeclarationTarget>> CollectDeclarationTargetsAsync(
        IReadOnlyList<IMethodSymbol> methods,
        ITypeSymbol newReturnType,
        bool convertReturnStatements,
        CancellationToken cancellationToken)
    {
        var targets = new List<DeclarationTarget>();

        foreach (var method in methods)
        {
            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                if (await syntaxRef.GetSyntaxAsync(cancellationToken) is not MethodDeclarationSyntax declaration)
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"Method '{method.Name}' is an unsupported target for change_return_type.");
                }

                var document = Context.Solution.GetDocument(syntaxRef.SyntaxTree)
                    ?? throw new RefactoringException(
                        ErrorCodes.DocumentNotEditable,
                        $"Could not locate the document for method '{method.Name}'.");

                var model = await document.GetSemanticModelAsync(cancellationToken)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

                if (IsIterator(declaration))
                {
                    throw new RefactoringException(
                        ErrorCodes.ContainsYield,
                        $"Method '{method.Name}' is an iterator; yield return types cannot be converted by change_return_type.");
                }

                var plan = AnalyzeReturnConversion(
                    declaration,
                    model,
                    method.ReturnType,
                    newReturnType,
                    convertReturnStatements);

                targets.Add(new DeclarationTarget(
                    document,
                    declaration.Span,
                    plan.ReturnSpans,
                    plan.Kind,
                    plan.AddTerminalDefault,
                    plan.ConvertExpressionBodyToBlock,
                    ToContextValidTypeName(newReturnType, model, declaration.ReturnType.SpanStart)));
            }
        }

        if (targets.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "The selected method is an unsupported target for change_return_type.");
        }

        return targets;
    }

    internal static ReturnRewritePlan AnalyzeReturnConversion(
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        ITypeSymbol currentReturnType,
        ITypeSymbol newReturnType,
        bool convertReturnStatements)
    {
        var currentIsVoid = IsVoid(currentReturnType, declaration.ReturnType);
        var newIsVoid = IsVoid(newReturnType);
        var returns = CollectReturnStatements(declaration);

        if (declaration.Body == null && declaration.ExpressionBody == null)
            return ReturnRewritePlan.DeclarationOnly;

        if (declaration.ExpressionBody != null)
        {
            return AnalyzeExpressionBody(
                declaration,
                model,
                currentIsVoid,
                newIsVoid,
                newReturnType,
                convertReturnStatements);
        }

        if (!currentIsVoid && !newIsVoid)
        {
            foreach (var ret in returns)
                EnsureReturnCompatible(ret, model, newReturnType, convertReturnStatements);

            return ReturnRewritePlan.DeclarationOnly;
        }

        if (!currentIsVoid && newIsVoid)
        {
            if (!convertReturnStatements && returns.Any(r => r.Expression != null))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvertReturn,
                    "Return statements cannot be converted to void because convertReturnStatements is false.");
            }

            return new ReturnRewritePlan(
                ReturnRewriteKind.StripReturnExpressions,
                returns.Select(r => r.Span).ToList(),
                AddTerminalDefault: false,
                ConvertExpressionBodyToBlock: false);
        }

        if (!convertReturnStatements)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertReturn,
                "Return statements cannot be converted from void because convertReturnStatements is false.");
        }

        var addTerminal = false;
        if (declaration.Body != null)
        {
            var flow = model.AnalyzeControlFlow(declaration.Body);
            addTerminal = flow is not { Succeeded: true, EndPointIsReachable: false };
        }

        return new ReturnRewritePlan(
            ReturnRewriteKind.AddDefaultReturns,
            returns.Select(r => r.Span).ToList(),
            addTerminal,
            ConvertExpressionBodyToBlock: false);
    }

    private static ReturnRewritePlan AnalyzeExpressionBody(
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        bool currentIsVoid,
        bool newIsVoid,
        ITypeSymbol newReturnType,
        bool convertReturnStatements)
    {
        var expression = declaration.ExpressionBody!.Expression;
        if (expression is ThrowExpressionSyntax)
            return ReturnRewritePlan.DeclarationOnly;

        if (!currentIsVoid && !newIsVoid)
        {
            EnsureExpressionCompatible(expression, model, newReturnType);
            return ReturnRewritePlan.DeclarationOnly;
        }

        if (!currentIsVoid && newIsVoid)
        {
            if (!convertReturnStatements)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvertReturn,
                    "Expression-bodied return cannot be converted to void because convertReturnStatements is false.");
            }

            return new ReturnRewritePlan(
                ReturnRewriteKind.StripReturnExpressions,
                Array.Empty<TextSpan>(),
                AddTerminalDefault: false,
                ConvertExpressionBodyToBlock: true);
        }

        if (!convertReturnStatements)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertReturn,
                "Expression-bodied void method cannot gain a return value because convertReturnStatements is false.");
        }

        return new ReturnRewritePlan(
            ReturnRewriteKind.AddDefaultReturns,
            Array.Empty<TextSpan>(),
            AddTerminalDefault: false,
            ConvertExpressionBodyToBlock: true);
    }

    private static void EnsureReturnCompatible(
        ReturnStatementSyntax ret,
        SemanticModel model,
        ITypeSymbol newReturnType,
        bool convertReturnStatements)
    {
        if (ret.Expression == null)
        {
            if (!convertReturnStatements)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvertReturn,
                    "A value-less return cannot be converted to the new return type.");
            }

            return;
        }

        if (ret.Expression is ThrowExpressionSyntax)
            return;

        EnsureExpressionCompatible(ret.Expression, model, newReturnType);
    }

    private static void EnsureExpressionCompatible(
        ExpressionSyntax expression,
        SemanticModel model,
        ITypeSymbol newReturnType)
    {
        var conversion = model.ClassifyConversion(expression, newReturnType);
        if (conversion.Exists && conversion.IsImplicit)
            return;

        throw new RefactoringException(
            ErrorCodes.ReturnTypeIncompatible,
            "New return type is not compatible with existing return statements.");
    }

    internal static IReadOnlyList<ReturnStatementSyntax> CollectReturnStatements(MethodDeclarationSyntax declaration)
    {
        if (declaration.Body == null)
            return Array.Empty<ReturnStatementSyntax>();

        return declaration.Body.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(r => !IsInNestedFunction(r, declaration))
            .ToList();
    }

    internal static bool IsVoid(ITypeSymbol type, TypeSyntax? syntax = null)
    {
        if (type.SpecialType == SpecialType.System_Void)
            return true;

        if (syntax is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return true;
        }

        return type.ToDisplayString() == "void";
    }

    private static bool IsInNestedFunction(SyntaxNode node, MethodDeclarationSyntax method)
    {
        for (var current = node.Parent; current != null && current != method; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return true;
        }

        return false;
    }

    private async Task ValidateReferencesAsync(
        IReadOnlyList<IMethodSymbol> methods,
        ITypeSymbol newReturnType,
        CancellationToken cancellationToken)
    {
        foreach (var method in methods)
        {
            var references = await SymbolFinder.FindReferencesAsync(method, Context.Solution, cancellationToken);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.Location.Kind != LocationKind.SourceFile)
                        continue;

                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null)
                        continue;

                    var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    if (IsDeclarationName(node, location.Location.SourceSpan))
                        continue;

                    var model = await document.GetSemanticModelAsync(cancellationToken)
                        ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation != null && IsInvokedMethodName(invocation, location.Location.SourceSpan))
                    {
                        ValidateInvocationResultContext(invocation, newReturnType, model);
                        continue;
                    }

                    if (IsNameOfArgument(node))
                        continue;

                    if (MethodGroupStillCompatible(node, newReturnType, model))
                        continue;

                    throw new RefactoringException(
                        ErrorCodes.UnsupportedCallSite,
                        $"Method '{method.Name}' is used as a method group or other unsupported reference and cannot be updated automatically.");
                }
            }
        }
    }

    internal static void ValidateInvocationResultContext(
        InvocationExpressionSyntax invocation,
        ITypeSymbol newReturnType,
        SemanticModel model)
    {
        if (IsUnusedInvocation(invocation) || IsVarInferredAssignment(invocation))
            return;

        var converted = model.GetTypeInfo(invocation).ConvertedType;
        if (converted == null || converted.TypeKind == TypeKind.Error)
            return;

        var conversion = model.Compilation.ClassifyConversion(newReturnType, converted);
        if (conversion.Exists && conversion.IsImplicit)
            return;

        throw new RefactoringException(
            ErrorCodes.ReturnTypeIncompatible,
            "New return type is not compatible with an invocation result context.");
    }

    internal static bool IsUnusedInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Parent is ExpressionStatementSyntax)
            return true;

        return invocation.Parent is AssignmentExpressionSyntax assignment &&
               assignment.Left is IdentifierNameSyntax identifier &&
               identifier.Identifier.ValueText == "_";
    }

    internal static bool IsVarInferredAssignment(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is EqualsValueClauseSyntax equals &&
               equals.Parent is VariableDeclaratorSyntax declarator &&
               declarator.Parent is VariableDeclarationSyntax declaration &&
               declaration.Type.IsVar;
    }

    internal static bool IsIterator(MethodDeclarationSyntax declaration)
    {
        SyntaxNode? body = declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody;
        if (body == null)
            return false;

        return body.DescendantNodesAndSelf()
            .OfType<YieldStatementSyntax>()
            .Any(yield => !IsInNestedFunction(yield, declaration));
    }

    internal static string ToContextValidTypeName(ITypeSymbol type, SemanticModel model, int position)
    {
        if (type.SpecialType == SpecialType.System_Void)
            return "void";

        var display = type.ToMinimalDisplayString(model, position);
        if (string.IsNullOrWhiteSpace(display) || TypeNameBindsToDifferentType(display, type, model, position))
        {
            display = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        }

        return display;
    }

    private static bool TypeNameBindsToDifferentType(
        string display,
        ITypeSymbol expected,
        SemanticModel model,
        int position)
    {
        var parsed = SyntaxFactory.ParseTypeName(display);
        var spec = model.GetSpeculativeTypeInfo(position, parsed, SpeculativeBindingOption.BindAsTypeOrNamespace);
        if (spec.Type == null || spec.Type.TypeKind == TypeKind.Error)
            return true;

        return !TypesEquivalent(spec.Type, expected);
    }

    internal static bool MethodGroupStillCompatible(
        SyntaxNode node,
        ITypeSymbol newReturnType,
        SemanticModel model)
    {
        foreach (var candidate in node.AncestorsAndSelf())
        {
            var converted = model.GetTypeInfo(candidate).ConvertedType;
            if (converted is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
                continue;

            if (invoke.ReturnType.SpecialType == SpecialType.System_Void)
                return true;

            var conversion = model.Compilation.ClassifyConversion(newReturnType, invoke.ReturnType);
            return conversion.Exists && conversion.IsImplicit;
        }

        return false;
    }

    private static async Task<Solution> ApplyChangesAsync(
        Document originatingDocument,
        IReadOnlyList<DeclarationTarget> declarations,
        CancellationToken cancellationToken)
    {
        var solution = originatingDocument.Project.Solution;
        var documentIds = declarations.Select(d => d.Document.Id).ToHashSet();

        foreach (var documentId in documentIds)
        {
            var document = solution.GetDocument(documentId)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var documentTargets = declarations.Where(d => d.Document.Id == documentId).ToList();
            var methodSpans = documentTargets.Select(d => d.MethodSpan).ToHashSet();
            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => methodSpans.Contains(m.Span))
                .ToList();

            if (methods.Count != documentTargets.Count)
            {
                throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    "Could not relocate symbol-resolved change_return_type declaration spans.");
            }

            var rewriter = new ChangeReturnTypeRewriter(documentTargets, methods);
            root = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite change_return_type targets.");

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    internal static TypeSyntax CreateReturnTypeSyntax(string newReturnType, TypeSyntax original)
    {
        return SyntaxFactory.ParseTypeName(newReturnType)
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());
    }

    internal static ReturnStatementSyntax StripReturnExpression(ReturnStatementSyntax statement)
    {
        if (statement.Expression == null)
            return statement;

        return statement
            .WithExpression(null)
            .WithReturnKeyword(statement.ReturnKeyword.WithTrailingTrivia());
    }

    internal static ReturnStatementSyntax AddDefaultReturnExpression(
        ReturnStatementSyntax statement,
        string newReturnType)
    {
        if (statement.Expression != null)
            return statement;

        var expression = CreateDefaultExpression(newReturnType);
        return statement
            .WithReturnKeyword(statement.ReturnKeyword.WithTrailingTrivia(SyntaxFactory.Space))
            .WithExpression(expression);
    }

    internal static ExpressionSyntax CreateDefaultExpression(string newReturnType)
    {
        return SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(newReturnType));
    }

    internal static ReturnStatementSyntax CreateDefaultReturnStatement(string newReturnType)
    {
        return SyntaxFactory.ReturnStatement(
            SyntaxFactory.Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            CreateDefaultExpression(newReturnType),
            SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    internal static MethodDeclarationSyntax ConvertExpressionBodyFromValueToVoid(MethodDeclarationSyntax method)
    {
        var returnStatement = SyntaxFactory.ReturnStatement();
        return method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(returnStatement))
            .NormalizeWhitespace();
    }

    internal static MethodDeclarationSyntax ConvertExpressionBodyFromVoidToValue(
        MethodDeclarationSyntax method,
        string newReturnType)
    {
        var expression = method.ExpressionBody!.Expression;
        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.ExpressionStatement(expression.WithoutTrivia()),
            CreateDefaultReturnStatement(newReturnType)
        };

        return method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(statements))
            .NormalizeWhitespace();
    }

    internal static MethodDeclarationSyntax AddTerminalDefaultReturn(
        MethodDeclarationSyntax method,
        string newReturnType)
    {
        if (method.Body == null)
            return method;

        var returnStatement = CreateDefaultReturnStatement(newReturnType)
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        return method.WithBody(method.Body.AddStatements(returnStatement));
    }

    private static bool NeedsTerminalDefault(MethodDeclarationSyntax method)
    {
        if (method.Body == null)
            return false;

        return !method.Body.Statements.OfType<ReturnStatementSyntax>().Any();
    }

    private static bool IsVoidSyntax(TypeSyntax type) =>
        type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        ChangeReturnTypeParams @params,
        Document originalDocument,
        Solution newSolution,
        CancellationToken cancellationToken)
    {
        var pendingChanges = new List<PendingChange>();
        var originalSolution = originalDocument.Project.Solution;

        foreach (var projectChanges in newSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = originalSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                if (oldDoc?.FilePath == null || newDoc == null)
                    continue;

                var before = await oldDoc.GetTextAsync(cancellationToken);
                var after = await newDoc.GetTextAsync(cancellationToken);
                pendingChanges.Add(new PendingChange
                {
                    File = oldDoc.FilePath,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Change return type of '{@params.MethodName}' to '{@params.NewReturnType}'",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Change return type of '{@params.MethodName}' to '{@params.NewReturnType}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsDeclarationName(SyntaxNode node, TextSpan referenceSpan)
    {
        return node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Span.IntersectsWith(referenceSpan));
    }

    private static bool IsInvokedMethodName(InvocationExpressionSyntax invocation, TextSpan referenceSpan)
    {
        if (invocation.ArgumentList.Span.Contains(referenceSpan))
            return false;

        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Span.IntersectsWith(referenceSpan),
            MemberAccessExpressionSyntax member => member.Name.Span.IntersectsWith(referenceSpan),
            MemberBindingExpressionSyntax binding => binding.Name.Span.IntersectsWith(referenceSpan),
            _ => invocation.Expression.Span.IntersectsWith(referenceSpan)
        };
    }

    private static bool IsNameOfArgument(SyntaxNode node)
    {
        foreach (var invocation in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == "nameof")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSourceDeclaration(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences.Length > 0 &&
        method.Locations.Any(l => l.IsInSource);

    private static bool ShareOverrideRoot(IMethodSymbol left, IMethodSymbol right) =>
        SymbolEqualityComparer.Default.Equals(GetOverrideRoot(left), GetOverrideRoot(right));

    private static IMethodSymbol GetOverrideRoot(IMethodSymbol method)
    {
        var current = method;
        while (current.OverriddenMethod != null)
            current = current.OverriddenMethod;
        return current;
    }

    internal readonly record struct ReturnRewritePlan(
        ReturnRewriteKind Kind,
        IReadOnlyList<TextSpan> ReturnSpans,
        bool AddTerminalDefault,
        bool ConvertExpressionBodyToBlock)
    {
        public static ReturnRewritePlan DeclarationOnly { get; } = new(
            ReturnRewriteKind.DeclarationOnly,
            Array.Empty<TextSpan>(),
            AddTerminalDefault: false,
            ConvertExpressionBodyToBlock: false);
    }

    internal enum ReturnRewriteKind
    {
        DeclarationOnly,
        StripReturnExpressions,
        AddDefaultReturns
    }

    private sealed record DeclarationTarget(
        Document Document,
        TextSpan MethodSpan,
        IReadOnlyList<TextSpan> ReturnSpans,
        ReturnRewriteKind Kind,
        bool AddTerminalDefault,
        bool ConvertExpressionBodyToBlock,
        string ReturnTypeText);

    private sealed class ChangeReturnTypeRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyList<DeclarationTarget> _targets;
        private readonly HashSet<MethodDeclarationSyntax> _methods;

        public ChangeReturnTypeRewriter(
            IReadOnlyList<DeclarationTarget> targets,
            IReadOnlyList<MethodDeclarationSyntax> methods)
        {
            _targets = targets;
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var original = _methods.FirstOrDefault(m => m.Span == node.Span);
            if (original == null)
                return base.VisitMethodDeclaration(node);

            var target = _targets.FirstOrDefault(t => t.MethodSpan == original.Span);
            if (target == null)
                return base.VisitMethodDeclaration(node);

            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            visited = visited.WithReturnType(CreateReturnTypeSyntax(target.ReturnTypeText, visited.ReturnType));

            var originalIsVoid = IsVoidSyntax(original.ReturnType);
            var newIsVoid = string.Equals(target.ReturnTypeText.Trim(), "void", StringComparison.Ordinal);
            var kind = target.Kind;
            if (kind == ReturnRewriteKind.DeclarationOnly && originalIsVoid && !newIsVoid)
                kind = ReturnRewriteKind.AddDefaultReturns;
            if (kind == ReturnRewriteKind.DeclarationOnly && !originalIsVoid && newIsVoid)
                kind = ReturnRewriteKind.StripReturnExpressions;

            if (kind == ReturnRewriteKind.AddDefaultReturns)
            {
                if (visited.ExpressionBody != null)
                    visited = ConvertExpressionBodyFromVoidToValue(visited, target.ReturnTypeText);
                else if (target.AddTerminalDefault || NeedsTerminalDefault(visited))
                    visited = AddTerminalDefaultReturn(visited, target.ReturnTypeText);
            }
            else if (kind == ReturnRewriteKind.StripReturnExpressions &&
                     visited.ExpressionBody != null)
            {
                visited = ConvertExpressionBodyFromValueToVoid(visited);
            }

            return visited;
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
            var target = _targets.FirstOrDefault(t => t.ReturnSpans.Any(span => span == node.Span));
            if (target == null)
                return visited;

            return target.Kind switch
            {
                ReturnRewriteKind.StripReturnExpressions => StripReturnExpression(visited),
                ReturnRewriteKind.AddDefaultReturns => AddDefaultReturnExpression(visited, target.ReturnTypeText),
                _ => visited
            };
        }
    }
}

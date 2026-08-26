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
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Adds the <c>static</c> modifier to a selected instance method that does not
/// use instance state, and updates call sites and method-group conversions to
/// use the containing type name instead of an instance.
/// </summary>
public sealed class MakeStaticOperation : RefactoringOperationBase<MakeStaticParams>
{
    /// <summary>
    /// Creates a new make-static operation.
    /// </summary>
    public MakeStaticOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(MakeStaticParams @params) => Validate(@params);

    /// <summary>
    /// Validates make-static parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(MakeStaticParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.StartColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (@params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.EndColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (@params.EndLine < @params.StartLine ||
            (@params.EndLine == @params.StartLine && @params.EndColumn < @params.StartColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

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
        MakeStaticParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = GetSelectionSpan(sourceText, @params);
        var symbol = ResolveSelectedSymbol(root, semanticModel, span, @params, cancellationToken);
        var method = NormalizeMethodSymbol(symbol);
        ValidateMethodCanBeMadeStatic(method);

        var declarationDocuments = await GetDeclarationDocumentsAsync(method, cancellationToken);
        foreach (var declarationDocument in declarationDocuments)
            ValidateDocumentIsEditable(declarationDocument, Context.Workspace);

        var instanceMembers = await GetInstanceMemberReferencesAsync(method, cancellationToken);
        if (!CanMakeStatic(instanceMembers))
            throw CreateUsesInstanceMembersException(method, instanceMembers);

        var plan = await BuildPlanAsync(method, cancellationToken);
        if (@params.Preview)
            return CreatePreviewResult(operationId, plan);

        var newSolution = await ApplyPlanAsync(Context.Solution, plan, cancellationToken);
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
                Name = method.Name,
                FullyQualifiedName = method.ToDisplayString(),
                Kind = SymbolKindMapper.Map(method)
            },
            plan.CallSites.Count,
            0);
    }

    internal static TextSpan GetSelectionSpan(SourceText sourceText, MakeStaticParams @params)
    {
        if (@params.StartLine > sourceText.Lines.Count || @params.EndLine > sourceText.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Selection is outside the file.");

        var startLine = sourceText.Lines[@params.StartLine - 1];
        var endLine = sourceText.Lines[@params.EndLine - 1];
        if (@params.StartColumn - 1 > startLine.Span.Length || @params.EndColumn - 1 > endLine.SpanIncludingLineBreak.Length)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Selection column is outside the line.");

        var startPosition = startLine.Start + @params.StartColumn - 1;
        var endPosition = endLine.Start + @params.EndColumn - 1;
        if (endPosition < startPosition)
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    private static ISymbol ResolveSelectedSymbol(
        SyntaxNode root,
        SemanticModel semanticModel,
        TextSpan span,
        MakeStaticParams @params,
        CancellationToken cancellationToken)
    {
        var token = root.FindToken(span.Start);
        if (token.Span.OverlapsWith(span) || span.OverlapsWith(token.Span))
        {
            var tokenNode = token.Parent;
            if (tokenNode != null)
            {
                var declaredOnToken = semanticModel.GetDeclaredSymbol(tokenNode, cancellationToken);
                if (declaredOnToken != null && IdentifierOverlaps(tokenNode, span))
                    return ConfirmSymbolName(declaredOnToken, @params.SymbolName);

                if (token.IsKind(SyntaxKind.IdentifierToken))
                {
                    var tokenSymbol = semanticModel.GetSymbolInfo(tokenNode, cancellationToken).Symbol;
                    if (tokenSymbol != null)
                        return ConfirmSymbolName(tokenSymbol, @params.SymbolName);
                }
            }
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var declared = semanticModel.GetDeclaredSymbol(node, cancellationToken);
        if (declared != null && IdentifierOverlaps(node, span))
            return ConfirmSymbolName(declared, @params.SymbolName);

        throw new RefactoringException(
            ErrorCodes.SymbolNotFound,
            "No symbol found at the specified selection.");
    }

    private static ISymbol ConfirmSymbolName(ISymbol symbol, string? expectedName)
    {
        if (!string.IsNullOrWhiteSpace(expectedName) && symbol.Name != expectedName)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{expectedName}' found at the specified selection.");
        }

        return symbol;
    }

    private static bool IdentifierOverlaps(SyntaxNode node, TextSpan span)
    {
        var identifier = GetDeclarationIdentifier(node);
        return identifier != null &&
            (identifier.Value.Span.OverlapsWith(span) || span.OverlapsWith(identifier.Value.Span));
    }

    private static SyntaxToken? GetDeclarationIdentifier(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        OperatorDeclarationSyntax @operator => @operator.OperatorToken,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetLastToken(),
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        VariableDeclaratorSyntax variable => variable.Identifier,
        TypeDeclarationSyntax type => type.Identifier,
        ParameterSyntax parameter => parameter.Identifier,
        _ => null
    };

    private static IMethodSymbol NormalizeMethodSymbol(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;

        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated } &&
            associated.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or Microsoft.CodeAnalysis.SymbolKind.Event)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{associated.Name}' is not a method.");
        }

        if (symbol is not IMethodSymbol method)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{symbol.Name}' is not a method.");
        }

        return method;
    }

    private static void ValidateMethodCanBeMadeStatic(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{method.Name}' is not an ordinary method that can be made static.");
        }

        if (method.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.AlreadyStatic,
                $"Method '{method.Name}' is already static.");
        }

        if (method.IsAbstract || method.IsOverride || HasVirtualModifier(method))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' cannot be made static because it is virtual, override, or abstract.");
        }

        if (method.ContainingType == null)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' has no containing type.");
        }

        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' is an interface member and cannot be made static.");
        }

        if (ImplementsInterface(method))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' implements an interface and cannot be made static.");
        }

        if (!method.Locations.Any(location => location.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Method '{method.Name}' is not in an editable document.");
        }
    }

    private static bool HasVirtualModifier(IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is MethodDeclarationSyntax declaration &&
                declaration.Modifiers.Any(SyntaxKind.VirtualKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsInterface(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Length > 0)
            return true;

        if (method.ContainingType == null)
            return false;

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                var implementation = method.ContainingType.FindImplementationForInterfaceMember(member);
                if (implementation == null)
                    continue;

                if (SymbolEqualityComparer.Default.Equals(implementation, method) ||
                    SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<Document>> GetDeclarationDocumentsAsync(
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var documents = new List<Document>();
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var document = Context.Solution.GetDocument(syntax.SyntaxTree);
            if (document == null)
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Declaration of '{method.Name}' is not in an editable document.");
            }

            documents.Add(document);
        }

        if (documents.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Method '{method.Name}' is not in an editable document.");
        }

        return documents;
    }

    internal static bool CanMakeStatic(IReadOnlyList<ISymbol> instanceMembers) => instanceMembers.Count == 0;

    private async Task<IReadOnlyList<ISymbol>> GetInstanceMemberReferencesAsync(
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var members = new List<ISymbol>();
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var document = Context.Solution.GetDocument(syntax.SyntaxTree);
            if (document == null)
                continue;

            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null)
                continue;

            members.AddRange(GetInstanceMemberReferences(method, model, syntax, cancellationToken));
        }

        return members
            .Distinct<ISymbol>(SymbolEqualityComparer.Default)
            .ToList();
    }

    internal static IReadOnlyList<ISymbol> GetInstanceMemberReferences(
        IMethodSymbol method,
        SemanticModel model,
        SyntaxNode methodSyntax,
        CancellationToken cancellationToken)
    {
        var body = GetMethodBody(methodSyntax);
        if (body == null)
            return Array.Empty<ISymbol>();

        var results = new List<ISymbol>();
        foreach (var node in body.DescendantNodesAndSelf())
        {
            if (IsInNameof(node))
                continue;

            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
            {
                if (!IsReceiverOfTargetMethod(node, method, model, cancellationToken))
                    results.Add(method.ContainingType);
                continue;
            }

            if (node is not SimpleNameSyntax name)
                continue;

            var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
            if (symbol == null || symbol.IsStatic)
                continue;

            if (SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, method.OriginalDefinition))
                continue;

            if (!IsInstanceStateSymbol(symbol))
                continue;

            if (IsMemberAccessName(name))
            {
                var receiver = GetReceiver(name);
                if (receiver is ThisExpressionSyntax or BaseExpressionSyntax)
                    results.Add(symbol);
                continue;
            }

            results.Add(symbol);
        }

        return results;
    }

    private static SyntaxNode? GetMethodBody(SyntaxNode methodSyntax) => methodSyntax switch
    {
        MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
        LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
        _ => methodSyntax
    };

    private static bool IsInNameof(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == "nameof")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInstanceStateSymbol(ISymbol symbol)
    {
        if (symbol is IParameterSymbol parameter &&
            parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
        {
            return true;
        }

        return symbol.Kind is Microsoft.CodeAnalysis.SymbolKind.Field
            or Microsoft.CodeAnalysis.SymbolKind.Property
            or Microsoft.CodeAnalysis.SymbolKind.Event
            or Microsoft.CodeAnalysis.SymbolKind.Method;
    }

    private static bool IsMemberAccessName(SimpleNameSyntax name) =>
        name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name ||
        name.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == name;

    private static ExpressionSyntax? GetReceiver(SimpleNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax memberAccess when memberAccess.Name == name => memberAccess.Expression,
        MemberBindingExpressionSyntax memberBinding when memberBinding.Name == name =>
            memberBinding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
        _ => null
    };

    private static bool IsReceiverOfTargetMethod(
        SyntaxNode receiver,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        SimpleNameSyntax? name = receiver.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess when memberAccess.Expression == receiver => memberAccess.Name,
            ConditionalAccessExpressionSyntax conditional when conditional.Expression == receiver =>
                GetConditionalBindingName(conditional),
            _ => null
        };

        if (name == null)
            return false;

        return model.GetSymbolInfo(name, cancellationToken).Symbol is IMethodSymbol referenced &&
            SymbolEqualityComparer.Default.Equals(referenced.OriginalDefinition, method.OriginalDefinition);
    }

    private static SimpleNameSyntax? GetConditionalBindingName(ConditionalAccessExpressionSyntax conditional) =>
        conditional.WhenNotNull switch
        {
            MemberBindingExpressionSyntax binding => binding.Name,
            InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax binding =>
                binding.Name,
            _ => null
        };

    private static RefactoringException CreateUsesInstanceMembersException(
        IMethodSymbol method,
        IReadOnlyList<ISymbol> instanceMembers)
    {
        var names = instanceMembers
            .Select(member => member.ToDisplayString())
            .Distinct()
            .ToList();

        return new RefactoringException(
            ErrorCodes.UsesInstanceMembers,
            $"Cannot make '{method.Name}' static because it uses instance members.",
            new Dictionary<string, object>
            {
                ["symbolName"] = method.Name,
                ["members"] = names
            },
            ["Remove instance member access (including implicit this) or keep the method as an instance method."]);
    }

    private async Task<StaticPlan> BuildPlanAsync(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var declarations = new List<DeclarationEdit>();
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            if (syntax is not MethodDeclarationSyntax declaration)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidSymbolKind,
                    $"Declaration '{method.Name}' is not a method that can be made static.");
            }

            declarations.Add(new DeclarationEdit(
                declaration.SyntaxTree,
                declaration.Span,
                GetSignatureSnippet(declaration),
                GetSignatureSnippet(AddStaticModifier(declaration))));
        }

        if (declarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate a declaration to make static for '{method.Name}'.");
        }

        var callSites = await FindCallSiteEditsAsync(method, cancellationToken);
        return new StaticPlan(
            method.Name,
            method.ToDisplayString(),
            SymbolKindMapper.Map(method),
            declarations,
            callSites);
    }

    private async Task<IReadOnlyList<CallSiteEdit>> FindCallSiteEditsAsync(
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(method, Context.Solution, cancellationToken);
        var edits = new List<CallSiteEdit>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Document == null || !location.Location.IsInSource)
                    continue;

                if (IsDefinitionLocation(referencedSymbol.Definition, location.Location))
                    continue;

                ValidateDocumentIsEditable(location.Document, Context.Workspace);

                var root = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var model = await location.Document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                    continue;

                var nameNode = FindReferencedName(root, location.Location.SourceSpan);
                if (nameNode == null)
                    continue;

                var target = GetRewriteTarget(nameNode);
                if (target == null)
                    continue;

                var receiver = GetReceiver(nameNode);
                var replacement = CreateStaticMemberAccess(
                    method.ContainingType,
                    nameNode,
                    model,
                    target.SpanStart,
                    receiver);
                var replacementWithTrivia = replacement.WithTriviaFrom(target);

                edits.Add(new CallSiteEdit(
                    location.Document.FilePath ?? string.Empty,
                    target.SyntaxTree,
                    target.Span,
                    target.ToString(),
                    replacementWithTrivia.ToString()));
            }
        }

        return edits;
    }

    private static SimpleNameSyntax? FindReferencedName(SyntaxNode root, TextSpan span)
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        return node as SimpleNameSyntax
            ?? node.AncestorsAndSelf().OfType<SimpleNameSyntax>()
                .FirstOrDefault(name => name.Span.Contains(span) || span.Contains(name.Span));
    }

    private static SyntaxNode? GetRewriteTarget(SimpleNameSyntax name)
    {
        if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
            return memberAccess;

        if (name.Parent is MemberBindingExpressionSyntax)
        {
            return name.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()
                ?? name.Parent;
        }

        return null;
    }

    private static ExpressionSyntax CreateStaticMemberAccess(
        INamedTypeSymbol containingType,
        SimpleNameSyntax name,
        SemanticModel model,
        int position,
        ExpressionSyntax? receiver)
    {
        var type = containingType;
        if (receiver != null &&
            model.GetTypeInfo(receiver).Type is INamedTypeSymbol receiverType &&
            SymbolEqualityComparer.Default.Equals(receiverType.OriginalDefinition, containingType.OriginalDefinition))
        {
            type = receiverType;
        }

        var typeName = type.ToMinimalDisplayString(model, position);
        var typeSyntax = SyntaxFactory.ParseName(typeName);
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            typeSyntax,
            (SimpleNameSyntax)name.WithoutTrivia());
    }

    private static bool IsDefinitionLocation(ISymbol symbol, Location location)
    {
        return symbol.Locations.Any(definition =>
            definition.IsInSource &&
            definition.SourceTree == location.SourceTree &&
            definition.SourceSpan == location.SourceSpan);
    }

    private static async Task<Solution> ApplyPlanAsync(
        Solution solution,
        StaticPlan plan,
        CancellationToken cancellationToken)
    {
        var trees = plan.Declarations.Select(declaration => declaration.SyntaxTree)
            .Concat(plan.CallSites.Select(callSite => callSite.SyntaxTree))
            .Distinct();

        foreach (var tree in trees)
        {
            var document = solution.GetDocument(tree)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    "A declaration or call-site document is no longer in the workspace.");

            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var callSiteSpans = plan.CallSites
                .Where(callSite => callSite.SyntaxTree == tree)
                .Select(callSite => callSite.Span)
                .ToHashSet();
            var declarationSpans = plan.Declarations
                .Where(declaration => declaration.SyntaxTree == tree)
                .Select(declaration => declaration.Span)
                .ToHashSet();

            var rewriteTargets = root.DescendantNodes()
                .Where(node => callSiteSpans.Contains(node.Span) &&
                    (node is MemberAccessExpressionSyntax || node is ConditionalAccessExpressionSyntax))
                .ToList();
            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => declarationSpans.Contains(method.Span))
                .ToList();

            SemanticModel? model = null;
            if (rewriteTargets.Count > 0)
            {
                model = await document.GetSemanticModelAsync(cancellationToken)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not get semantic model.");
            }

            var replacements = rewriteTargets.ToDictionary(
                target => target.Span,
                target => RewriteCallSite(target, model!, plan.Name));

            var methodAnn = new SyntaxAnnotation("make-static-method");
            var rewriteAnn = new SyntaxAnnotation("make-static-call");
            var methodSet = methods.Cast<SyntaxNode>().ToHashSet();
            var rewriteSet = rewriteTargets.ToHashSet();
            var annotateTargets = methodSet.Concat(rewriteSet).ToList();
            if (annotateTargets.Count > 0)
            {
                root = root.ReplaceNodes(annotateTargets, (original, _) =>
                {
                    var node = original;
                    if (methodSet.Contains(original))
                        node = node.WithAdditionalAnnotations(methodAnn);
                    if (rewriteSet.Contains(original))
                        node = node.WithAdditionalAnnotations(rewriteAnn);
                    return node;
                });
            }

            var annotatedRewrites = root.GetAnnotatedNodes(rewriteAnn).ToList();
            if (annotatedRewrites.Count > 0)
            {
                root = root.ReplaceNodes(annotatedRewrites, (original, _) =>
                    replacements.TryGetValue(original.Span, out var replacement)
                        ? replacement
                        : original);
            }

            var annotatedMethods = root.GetAnnotatedNodes(methodAnn).OfType<MethodDeclarationSyntax>().ToList();
            if (annotatedMethods.Count > 0)
                root = root.ReplaceNodes(annotatedMethods, (original, _) => AddStaticModifier(original));

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    private static SyntaxNode RewriteCallSite(SyntaxNode original, SemanticModel model, string methodName)
    {
        SimpleNameSyntax? name = original switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            ConditionalAccessExpressionSyntax conditional => GetConditionalBindingName(conditional),
            _ => null
        };

        if (name == null)
            return original;

        ExpressionSyntax? receiver = original switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            ConditionalAccessExpressionSyntax conditional => conditional.Expression,
            _ => null
        };

        var symbol = model.GetSymbolInfo(name).Symbol;
        var containingType = (symbol as IMethodSymbol)?.ContainingType;
        if (containingType == null)
        {
            var fallback = SyntaxFactory.ParseName(methodName);
            return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    fallback,
                    (SimpleNameSyntax)name.WithoutTrivia())
                .WithTriviaFrom(original);
        }

        if (original is ConditionalAccessExpressionSyntax conditionalAccess &&
            conditionalAccess.WhenNotNull is InvocationExpressionSyntax invocation)
        {
            var staticAccess = CreateStaticMemberAccess(
                containingType, name, model, original.SpanStart, receiver);
            return invocation
                .WithExpression(staticAccess)
                .WithTriviaFrom(original);
        }

        return CreateStaticMemberAccess(containingType, name, model, original.SpanStart, receiver)
            .WithTriviaFrom(original);
    }

    internal static MethodDeclarationSyntax AddStaticModifier(MethodDeclarationSyntax method)
    {
        if (method.Modifiers.Any(SyntaxKind.StaticKeyword))
            return method;

        var staticToken = SyntaxFactory.Token(SyntaxKind.StaticKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        var modifiers = method.Modifiers;
        var insertIndex = 0;
        for (var i = 0; i < modifiers.Count; i++)
        {
            if (modifiers[i].IsKind(SyntaxKind.PublicKeyword) ||
                modifiers[i].IsKind(SyntaxKind.PrivateKeyword) ||
                modifiers[i].IsKind(SyntaxKind.ProtectedKeyword) ||
                modifiers[i].IsKind(SyntaxKind.InternalKeyword))
            {
                insertIndex = i + 1;
            }
        }

        if (insertIndex == 0 && modifiers.Count > 0)
        {
            var first = modifiers[0];
            staticToken = staticToken.WithLeadingTrivia(first.LeadingTrivia);
            modifiers = modifiers.Replace(first, first.WithLeadingTrivia(SyntaxFactory.TriviaList()));
            return method.WithModifiers(modifiers.Insert(0, staticToken));
        }

        if (insertIndex == 0)
            return method.WithModifiers(SyntaxFactory.TokenList(staticToken));

        return method.WithModifiers(modifiers.Insert(insertIndex, staticToken));
    }

    private static string GetSignatureSnippet(MethodDeclarationSyntax method)
    {
        var returnType = method.ReturnType.ToString();
        var modifiers = string.Join(" ", method.Modifiers.Select(token => token.Text));
        var signature = $"{modifiers} {returnType} {method.Identifier}{method.TypeParameterList}{method.ParameterList}";
        return signature.Trim();
    }

    private static RefactoringResult CreatePreviewResult(Guid operationId, StaticPlan plan)
    {
        var pendingChanges = new List<PendingChange>();
        foreach (var declaration in plan.Declarations)
        {
            pendingChanges.Add(new PendingChange
            {
                File = declaration.SyntaxTree.FilePath ?? string.Empty,
                ChangeType = ChangeKind.Modify,
                Description = $"Add static modifier to '{plan.Name}'",
                BeforeSnippet = declaration.Before,
                AfterSnippet = declaration.After
            });
        }

        foreach (var callSite in plan.CallSites)
        {
            pendingChanges.Add(new PendingChange
            {
                File = callSite.File,
                ChangeType = ChangeKind.Modify,
                Description = $"Update call site of '{plan.Name}' to use the type name",
                BeforeSnippet = callSite.Before,
                AfterSnippet = callSite.After
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record DeclarationEdit(SyntaxTree SyntaxTree, TextSpan Span, string Before, string After);

    private sealed record CallSiteEdit(
        string File,
        SyntaxTree SyntaxTree,
        TextSpan Span,
        string Before,
        string After);

    private sealed record StaticPlan(
        string Name,
        string FullyQualifiedName,
        Contracts.Enums.SymbolKind Kind,
        IReadOnlyList<DeclarationEdit> Declarations,
        IReadOnlyList<CallSiteEdit> CallSites);
}

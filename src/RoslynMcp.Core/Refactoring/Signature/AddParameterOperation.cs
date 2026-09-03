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
/// Adds a named parameter to a method and updates call sites, overrides,
/// and interface implementations.
/// </summary>
public sealed class AddParameterOperation : RefactoringOperationBase<AddParameterParams>
{
    /// <summary>
    /// Creates a new add parameter operation.
    /// </summary>
    public AddParameterOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AddParameterParams @params) => Validate(@params);

    /// <summary>
    /// Validates add-parameter inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(AddParameterParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (string.IsNullOrWhiteSpace(@params.ParameterName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameterName is required.");

        if (string.IsNullOrWhiteSpace(@params.ParameterType))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameterType is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidIdentifier(@params.ParameterName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"'{@params.ParameterName}' is not a valid parameter name.");

        if (!IsValidParameterType(@params.ParameterType))
            throw new RefactoringException(ErrorCodes.InvalidParameterType, $"'{@params.ParameterType}' is not a valid C# parameter type.");

        if (@params.Position < -1)
            throw new RefactoringException(ErrorCodes.InvalidParameterPosition, "position must be -1 (end) or a 0-based index.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.DefaultValue != null && !IsValidDefaultValueExpression(@params.DefaultValue))
            throw new RefactoringException(ErrorCodes.InvalidDefaultValue, $"'{@params.DefaultValue}' is not a valid default value expression.");
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
        AddParameterParams @params,
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

        if (methodSymbol.Parameters.Any(p => p.Name == NormalizeIdentifier(@params.ParameterName)))
        {
            throw new RefactoringException(
                ErrorCodes.ParameterAlreadyExists,
                $"Parameter '{@params.ParameterName}' already exists in method '{@params.MethodName}'.");
        }

        var insertIndex = ComputeInsertionIndex(methodDecl.ParameterList, @params.Position);
        var attachDefaultToDeclaration = CanAttachDefaultToDeclaration(methodDecl.ParameterList, insertIndex);
        ValidateResultingSignature(methodDecl.ParameterList, insertIndex, @params, attachDefaultToDeclaration);

        if (!string.IsNullOrEmpty(@params.DefaultValue))
        {
            ValidateDefaultValue(
                semanticModel,
                @params.ParameterType,
                @params.DefaultValue);
        }

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            cancellationToken);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, cancellationToken);
        foreach (var target in declarationTargets)
            ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var callSites = await CollectCallSitesAsync(relatedMethods, cancellationToken);
        foreach (var callSite in callSites)
            ValidateDocumentIsEditable(callSite.Document, Context.Workspace);

        var declarationDefault = attachDefaultToDeclaration ? @params.DefaultValue : null;
        var newParameter = CreateParameter(NormalizeIdentifier(@params.ParameterName), @params.ParameterType, declarationDefault);
        var appending = insertIndex >= methodSymbol.Parameters.Length;

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            methodSymbol.Parameters.ToList(),
            insertIndex,
            newParameter,
            @params.ParameterName,
            @params.DefaultValue,
            appending,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                insertIndex,
                callSites.Count,
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
            callSites.Count,
            0);
    }

    internal static MethodDeclarationSyntax FindMethodDeclaration(SyntaxNode root, AddParameterParams @params)
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

        // Line is required when more than one method matches, even if
        // column is set. Column without Line is not a source position:
        // FindMethod would substitute each candidate's own start line and
        // could silently pick the shortest equally-aligned overload.
        // When both are set, pick by identifier/declaration span and do
        // not require the declaration to start on `line` (continuation-
        // line identifier).
        if (methods.Count > 1 && !@params.Line.HasValue)
        {
            var lines = methods
                .Select(StartLine)
                .ToList();
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
        }

        if (@params.Column.HasValue)
        {
            var covering = FindMethod(root, @params.MethodName, @params.Line, @params.Column);
            if (covering == null)
            {
                throw new RefactoringException(
                    ErrorCodes.MethodNotFound,
                    @params.Line.HasValue
                        ? $"Method '{@params.MethodName}' not found at line {@params.Line}."
                        : $"Method '{@params.MethodName}' not found.");
            }

            return covering;
        }

        // Omitted column keeps today's MethodName + optional Line start-line
        // pick exactly. Do not force column 1. Do not rewrite line-only to
        // covering-span. A single name match with no line is used as-is;
        // line filters declaration start-line; several start-line hits stay
        // SymbolAmbiguous (do not FirstOrDefault the first same-line overload).
        if (methods.Count == 1 && !@params.Line.HasValue)
            return methods[0];

        IEnumerable<MethodDeclarationSyntax> filtered = methods;
        if (@params.Line.HasValue)
        {
            filtered = filtered.Where(m => StartLine(m) == @params.Line.Value);
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

        var optionLines = matches
            .Select(StartLine)
            .ToList();
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", optionLines)}");
    }

    /// <summary>
    /// Finds a method. Omitted <paramref name="column"/> keeps today's
    /// MethodName + optional Line start-line pick (a single name match
    /// with no line is used as-is; line uses start-line equality; several
    /// start-line hits stay ambiguous at the caller). When set, picks the
    /// smallest method whose identifier or declaration span covers that
    /// 1-based column. Do not require the declaration to start on
    /// <paramref name="line"/> when column is set — a split signature may
    /// put the identifier on a continuation line.
    /// </summary>
    internal static MethodDeclarationSyntax? FindMethod(
        SyntaxNode root,
        string methodName,
        int? line,
        int? column)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (column.HasValue)
        {
            // When column is set, do not require the declaration to start
            // on `line` — a split signature's identifier may live on a
            // continuation line whose declaration span still covers that
            // column. Prefer the identifier hit, then the smallest
            // containing declaration. Do not silently pick the first when
            // a covering node exists elsewhere — scan every candidate,
            // including those that do not start on `line`. If nothing
            // covers this position, keep today's not-found (null).
            return methods
                .Where(m => MethodCoversColumn(m, line ?? StartLine(m), column.Value))
                .OrderBy(m => IdentifierCoversColumn(m, line ?? StartLine(m), column.Value) ? 0 : 1)
                .ThenBy(m => m.Span.Length)
                .FirstOrDefault();
        }

        if (methods.Count == 1 && !line.HasValue)
            return methods[0];

        if (!line.HasValue)
            return methods.Count == 1 ? methods[0] : null;

        var startLineMatches = methods.Where(m => StartLine(m) == line.Value).ToList();
        return startLineMatches.Count == 1 ? startLineMatches[0] : null;
    }

    private static int StartLine(MethodDeclarationSyntax method) =>
        method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool MethodCoversColumn(MethodDeclarationSyntax method, int line, int column) =>
        IdentifierCoversColumn(method, line, column) ||
        SpanCoversColumn(method.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(MethodDeclarationSyntax method, int line, int column) =>
        SpanCoversColumn(method.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent method also
    /// match the previous declaration. Same helper as
    /// <c>ChangeSignatureOperation.SpanCoversColumn</c>.
    /// </summary>
    internal static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column >= endCol)
            return false;
        return true;
    }

    internal static int ComputeInsertionIndex(ParameterListSyntax list, int position)
    {
        var count = list.Parameters.Count;
        if (position == -1)
        {
            for (var i = 0; i < count; i++)
            {
                if (IsParams(list.Parameters[i]) || IsOptional(list.Parameters[i]))
                    return i;
            }

            return count;
        }

        if (position < 0 || position > count)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterPosition,
                $"Position {position} is out of range. Valid range is 0 to {count}.");
        }

        return position;
    }

    internal static bool CanAttachDefaultToDeclaration(ParameterListSyntax original, int insertIndex)
    {
        for (var i = insertIndex; i < original.Parameters.Count; i++)
        {
            if (!IsOptional(original.Parameters[i]) && !IsParams(original.Parameters[i]))
                return false;
        }

        return true;
    }

    internal static void ValidateResultingSignature(
        ParameterListSyntax original,
        int insertIndex,
        AddParameterParams @params,
        bool attachDefaultToDeclaration)
    {
        var hypothetical = original.Parameters.ToList();
        hypothetical.Insert(insertIndex, CreateParameter(
            NormalizeIdentifier(@params.ParameterName),
            @params.ParameterType,
            attachDefaultToDeclaration ? @params.DefaultValue : null));

        var seenOptional = false;
        for (var i = 0; i < hypothetical.Count; i++)
        {
            var parameter = hypothetical[i];
            var isParams = IsParams(parameter);
            var isOptional = IsOptional(parameter);

            if (isParams && i != hypothetical.Count - 1)
            {
                throw new RefactoringException(
                    ErrorCodes.ParamsNotLast,
                    "A params parameter must remain last in the parameter list.");
            }

            if (isOptional)
            {
                seenOptional = true;
            }
            else if (seenOptional && !isParams)
            {
                throw new RefactoringException(
                    ErrorCodes.RequiredAfterOptional,
                    "Required parameters cannot follow optional parameters.");
            }
        }
    }

    internal static ParameterSyntax CreateParameter(string name, string type, string? defaultValue)
    {
        var paramSyntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
            .WithType(SyntaxFactory.ParseTypeName(type).WithTrailingTrivia(SyntaxFactory.Space));

        if (!string.IsNullOrEmpty(defaultValue))
        {
            paramSyntax = paramSyntax.WithDefault(
                SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.Token(SyntaxKind.EqualsToken)
                        .WithLeadingTrivia(SyntaxFactory.Space)
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.ParseExpression(defaultValue)));
        }

        return paramSyntax;
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

        return results.Where(HasSourceDeclaration).ToList();
    }

    private async Task<List<DeclarationTarget>> CollectDeclarationTargetsAsync(
        IReadOnlyList<IMethodSymbol> methods,
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
                        $"Method '{method.Name}' is an unsupported target for add_parameter.");
                }

                var document = Context.Solution.GetDocument(syntaxRef.SyntaxTree)
                    ?? throw new RefactoringException(
                        ErrorCodes.DocumentNotEditable,
                        $"Could not locate the document for method '{method.Name}'.");

                targets.Add(new DeclarationTarget(document, declaration.Span));
            }
        }

        if (targets.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "The selected method is an unsupported target for add_parameter.");
        }

        return targets;
    }

    private async Task<List<CallSite>> CollectCallSitesAsync(
        IReadOnlyList<IMethodSymbol> methods,
        CancellationToken cancellationToken)
    {
        var callSites = new List<CallSite>();
        var seen = new HashSet<(DocumentId Id, TextSpan Span)>();

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

                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation != null && IsInvokedMethodName(invocation, location.Location.SourceSpan))
                    {
                        if (!seen.Add((document.Id, invocation.Span)))
                            continue;

                        callSites.Add(new CallSite(document, invocation.Span));
                        continue;
                    }

                    if (IsNameOfArgument(node))
                        continue;

                    throw new RefactoringException(
                        ErrorCodes.UnsupportedCallSite,
                        $"Method '{method.Name}' is used as a method group or other unsupported reference and cannot be updated automatically.");
                }
            }
        }

        return callSites;
    }

    private static async Task<Solution> ApplyChangesAsync(
        Document originatingDocument,
        IReadOnlyList<DeclarationTarget> declarations,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<IParameterSymbol> originalParams,
        int insertIndex,
        ParameterSyntax newParameter,
        string parameterName,
        string? defaultValue,
        bool appending,
        CancellationToken cancellationToken)
    {
        var solution = originatingDocument.Project.Solution;
        var documentIds = declarations.Select(d => d.Document.Id)
            .Concat(callSites.Select(c => c.Document.Id))
            .ToHashSet();

        foreach (var documentId in documentIds)
        {
            var document = solution.GetDocument(documentId)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var declarationSpans = declarations
                .Where(d => d.Document.Id == documentId)
                .Select(d => d.Span)
                .ToHashSet();
            var invocationSpans = callSites
                .Where(c => c.Document.Id == documentId)
                .Select(c => c.Span)
                .ToHashSet();

            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => declarationSpans.Contains(m.Span))
                .ToList();
            var invocations = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => invocationSpans.Contains(i.Span))
                .ToList();

            var rewriter = new AddParameterRewriter(
                methods,
                invocations,
                insertIndex,
                newParameter,
                originalParams,
                parameterName,
                defaultValue,
                appending);
            root = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite add_parameter targets.");

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    internal static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax invocation,
        IReadOnlyList<IParameterSymbol> originalParams,
        int insertIndex,
        string parameterName,
        string? defaultValue,
        bool appending)
    {
        var originalArgs = invocation.ArgumentList.Arguments.ToList();
        var newArg = CreateArgument(parameterName, defaultValue);
        var useNamedInsertion = !appending || originalArgs.Any(a => a.NameColon != null);

        if (useNamedInsertion && newArg.NameColon == null)
        {
            newArg = newArg.WithNameColon(
                SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(NormalizeIdentifier(parameterName))));
        }

        var namedOriginal = new Dictionary<string, ArgumentSyntax>();
        var positionalOriginal = new List<ArgumentSyntax>();
        foreach (var arg in originalArgs)
        {
            if (arg.NameColon != null)
                namedOriginal[arg.NameColon.Name.Identifier.Text] = arg;
            else
                positionalOriginal.Add(arg);
        }

        var newArgs = new List<ArgumentSyntax>();
        var positionalIndex = 0;
        var namedNewArgInserted = false;

        for (var i = 0; i < originalParams.Count + 1; i++)
        {
            if (i == insertIndex)
            {
                newArgs.Add(newArg);
                namedNewArgInserted = newArg.NameColon != null;
                continue;
            }

            var originalIndex = i < insertIndex ? i : i - 1;
            if (originalIndex < 0 || originalIndex >= originalParams.Count)
                continue;

            var originalParam = originalParams[originalIndex];
            if (namedOriginal.TryGetValue(originalParam.Name, out var namedArg))
            {
                newArgs.Add(namedArg);
                continue;
            }

            if (positionalIndex >= positionalOriginal.Count)
                continue;

            var positional = positionalOriginal[positionalIndex++];
            if (namedNewArgInserted && positional.NameColon == null)
            {
                positional = positional.WithNameColon(
                    SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(originalParam.Name)));
            }

            newArgs.Add(positional);
        }

        while (positionalIndex < positionalOriginal.Count)
            newArgs.Add(positionalOriginal[positionalIndex++]);

        foreach (var leftover in namedOriginal.Values)
        {
            if (!newArgs.Contains(leftover))
                newArgs.Add(leftover);
        }

        return invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SeparatedWithSpaces(newArgs))
                .WithTriviaFrom(invocation.ArgumentList));
    }

    internal static ArgumentSyntax CreateArgument(string paramName, string? defaultValue)
    {
        var expression = SyntaxFactory.ParseExpression(
            string.IsNullOrEmpty(defaultValue) ? "default" : defaultValue);
        return SyntaxFactory.Argument(expression);
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        AddParameterParams @params,
        Document originalDocument,
        Solution newSolution,
        int insertIndex,
        int callSiteCount,
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
                    Description = $"Add parameter '{@params.ParameterName}' to '{@params.MethodName}' at position {insertIndex} ({callSiteCount} call site(s) to update)",
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
                Description = $"Add parameter '{@params.ParameterName}' to '{@params.MethodName}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

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

    internal static bool IsValidParameterType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        var parsed = SyntaxFactory.ParseTypeName(type);
        if (parsed.ContainsDiagnostics || parsed.IsMissing)
            return false;

        var remainder = type.Trim();
        var written = parsed.ToString();
        if (!string.Equals(written.Trim(), remainder, StringComparison.Ordinal))
        {
            // ParseTypeName is permissive; reject leftovers such as "int int".
            if (remainder.Length > written.Trim().Length)
                return false;
        }

        return parsed is not IdentifierNameSyntax identifier || !identifier.IsMissing;
    }

    internal static bool IsValidDefaultValueExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var expression = SyntaxFactory.ParseExpression(value);
        return !expression.ContainsDiagnostics && !expression.IsMissing;
    }

    internal static void ValidateDefaultValue(
        SemanticModel semanticModel,
        string parameterType,
        string defaultValue)
    {
        var expression = SyntaxFactory.ParseExpression(defaultValue);
        if (expression.IsMissing || expression.ContainsDiagnostics)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not a valid default value expression.");
        }

        var parseOptions = (CSharpParseOptions)semanticModel.SyntaxTree.Options;
        var probeTree = CSharpSyntaxTree.ParseText($$"""
            namespace __RoslynMcpProbe
            {
                static class Probe
                {
                    static void M()
                    {
                        {{parameterType}} __v = {{defaultValue}};
                    }
                }
            }
            """, parseOptions);

        var probeCompilation = semanticModel.Compilation.AddSyntaxTrees(probeTree);
        var probeModel = probeCompilation.GetSemanticModel(probeTree);
        var local = probeTree.GetRoot().DescendantNodes().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        var initializer = local?.Declaration.Variables.FirstOrDefault()?.Initializer?.Value;
        if (local == null || initializer == null)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not a valid default value expression.");
        }

        var targetType = probeModel.GetTypeInfo(local.Declaration.Type).Type;
        if (targetType == null || targetType.TypeKind == TypeKind.Error)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterType,
                $"'{parameterType}' is not a valid C# parameter type.");
        }

        var conversion = probeModel.ClassifyConversion(initializer, targetType);
        if (!conversion.Exists || (!conversion.IsIdentity && !conversion.IsImplicit))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not implicitly convertible to '{parameterType}'.");
        }

        if (IsDefaultValueExpression(initializer))
            return;

        if (!probeModel.GetConstantValue(initializer).HasValue)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not a compile-time constant or default(T).");
        }
    }

    private static bool IsDefaultValueExpression(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.DefaultLiteralExpression) ||
        expression is DefaultExpressionSyntax;

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

    private static bool IsParams(ParameterSyntax parameter) =>
        parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));

    private static bool IsOptional(ParameterSyntax parameter) =>
        parameter.Default != null;

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

    private static string NormalizeIdentifier(string name) =>
        name.StartsWith('@') && name.Length > 1 ? name[1..] : name;

    private static SeparatedSyntaxList<T> SeparatedWithSpaces<T>(IReadOnlyList<T> nodes)
        where T : SyntaxNode
    {
        if (nodes.Count == 0)
            return SyntaxFactory.SeparatedList<T>();

        var separators = nodes.Count == 1
            ? Array.Empty<SyntaxToken>()
            : Enumerable.Repeat(
                SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
                nodes.Count - 1).ToArray();

        return SyntaxFactory.SeparatedList(nodes, separators);
    }

    private sealed record DeclarationTarget(Document Document, TextSpan Span);

    private sealed record CallSite(Document Document, TextSpan Span);

    private sealed class AddParameterRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<MethodDeclarationSyntax> _methods;
        private readonly HashSet<InvocationExpressionSyntax> _invocations;
        private readonly int _insertIndex;
        private readonly ParameterSyntax _newParameter;
        private readonly IReadOnlyList<IParameterSymbol> _originalParams;
        private readonly string _parameterName;
        private readonly string? _defaultValue;
        private readonly bool _appending;

        public AddParameterRewriter(
            IReadOnlyList<MethodDeclarationSyntax> methods,
            IReadOnlyList<InvocationExpressionSyntax> invocations,
            int insertIndex,
            ParameterSyntax newParameter,
            IReadOnlyList<IParameterSymbol> originalParams,
            string parameterName,
            string? defaultValue,
            bool appending)
        {
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
            _invocations = new HashSet<InvocationExpressionSyntax>(invocations);
            _insertIndex = insertIndex;
            _newParameter = newParameter;
            _originalParams = originalParams;
            _parameterName = parameterName;
            _defaultValue = defaultValue;
            _appending = appending;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            var original = _methods.FirstOrDefault(m => m.Span == node.Span && m.Identifier.Text == node.Identifier.Text);
            if (original == null)
                return visited;

            var parameters = visited.ParameterList.Parameters.ToList();
            var insertAt = Math.Min(_insertIndex, parameters.Count);
            parameters.Insert(insertAt, _newParameter);
            return visited.WithParameterList(
                SyntaxFactory.ParameterList(SeparatedWithSpaces(parameters))
                    .WithTriviaFrom(visited.ParameterList));
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!_invocations.Contains(node) && !_invocations.Any(i => i.Span == node.Span))
                return visited;

            return UpdateInvocation(
                visited,
                _originalParams,
                _insertIndex,
                _parameterName,
                _defaultValue,
                _appending);
        }
    }
}

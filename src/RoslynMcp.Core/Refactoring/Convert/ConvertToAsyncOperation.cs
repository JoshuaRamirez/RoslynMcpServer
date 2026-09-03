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

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts a synchronous method to async/await pattern.
/// </summary>
public sealed class ConvertToAsyncOperation : RefactoringOperationBase<ConvertToAsyncParams>
{
    private static readonly SyntaxAnnotation AwaitableCallAnnotation = new("convert-to-async-awaitable");
    private static readonly SyntaxAnnotation SelfCallAnnotation = new("convert-to-async-self-call");
    private static readonly SyntaxAnnotation SelfCallAwaitAnnotation = new("convert-to-async-self-call-await");
    private const string ConvertedCallRenameKind = "convert-to-async-rename";

    internal const string SyncCallerSkipReason =
        "Caller is not async; skipped await wrap to keep the method compiling.";

    internal const string NotInvocationSkipReason =
        "Reference is not an invocation (method group or similar); skipped await wrap.";

    /// <summary>
    /// Creates a new convert to async operation.
    /// </summary>
    public ConvertToAsyncOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToAsyncParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-to-async parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertToAsyncParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.MethodName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with methodName, line, or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToAsyncParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        var methodName = @params.MethodName!;
        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (methodDeclarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{methodName}' not found.");
        }

        // Line is required when more than one method matches, even if
        // column is set. Column without Line is not a source position:
        // FindMethod would substitute each candidate's own start line and
        // could silently pick the shortest equally-aligned overload.
        // When both are set, pick by identifier/declaration span and do
        // not require the declaration to start on `line` (continuation-
        // line identifier).
        if (methodDeclarations.Count > 1 && !@params.Line.HasValue)
        {
            var lines = methodDeclarations
                .Select(m => m.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                .ToList();
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple methods named '{methodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
        }

        var methodDecl = FindMethod(root, methodName, @params.Line, @params.Column);
        if (methodDecl == null)
        {
            var location = @params.Column.HasValue
                ? @params.Line.HasValue
                    ? $"'{methodName}' at line {@params.Line}, column {@params.Column.Value}"
                    : $"'{methodName}' at column {@params.Column.Value}"
                : $"'{methodName}' at line {@params.Line}";
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method {location} not found.");
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");
        }

        // Check if already async
        if (methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            throw new RefactoringException(
                ErrorCodes.AlreadyAsync,
                "Method is already async.");
        }

        // Check for yield statements (iterators can't be async)
        if (methodDecl.DescendantNodes().OfType<YieldStatementSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertIterator,
                "Cannot convert iterator method to async.");
        }

        // Find awaitable calls in the method
        var awaitableCalls = FindAwaitableCalls(methodDecl, semanticModel, cancellationToken);

        if (awaitableCalls.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoAsyncCalls,
                "Method has no awaitable calls to convert.");
        }

        // Determine new method name
        var newMethodName = ResolveAsyncMethodName(methodName, @params.RenameToAsync);

        var needsRename = newMethodName != methodName;
        var callSites = needsRename || @params.UpdateCallers
            ? await CollectCallSitesAsync(
                methodSymbol,
                methodDecl,
                document,
                methodName,
                cancellationToken)
            : [];

        var plan = PlanCallerUpdates(callSites, @params.UpdateCallers);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                methodSymbol,
                newMethodName,
                awaitableCalls.Count,
                plan);
        }

        // Annotate body invocations before changing the signature. WithIdentifier /
        // AddModifiers shift spans, so the rewriter cannot match original TextSpans.
        var annotatedCalls = new Dictionary<InvocationExpressionSyntax, List<SyntaxAnnotation>>();
        foreach (var call in awaitableCalls)
            AddAnnotation(annotatedCalls, call, AwaitableCallAnnotation);
        foreach (var site in callSites.Where(site => site.IsInsideConvertedMethod && site.Invocation != null))
        {
            AddAnnotation(annotatedCalls, site.Invocation!, SelfCallAnnotation);
            if (ShouldAwaitCallSite(site, @params.UpdateCallers))
                AddAnnotation(annotatedCalls, site.Invocation!, SelfCallAwaitAnnotation);
        }

        var methodToRewrite = methodDecl;
        if (annotatedCalls.Count > 0)
        {
            methodToRewrite = methodDecl.ReplaceNodes(
                annotatedCalls.Keys,
                (original, _) => original.WithAdditionalAnnotations(annotatedCalls[original]));
        }

        methodToRewrite = (MethodDeclarationSyntax)new AnnotatedCallRewriter(
            methodName,
            newMethodName).Visit(methodToRewrite)!;

        var newMethod = methodToRewrite
            .WithIdentifier(SyntaxFactory.Identifier(newMethodName))
            .WithReturnType(SyntaxGenerationHelper.ToAsyncReturnType(methodSymbol.ReturnType)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var newSolution = document.Project.Solution;
        var sourceReplaced = false;
        var externalSites = callSites.Where(site => !site.IsInsideConvertedMethod).ToList();

        foreach (var group in externalSites.GroupBy(site => site.DocumentId))
        {
            var refDoc = newSolution.GetDocument(group.Key);
            if (refDoc == null) continue;

            var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
            if (refRoot == null) continue;

            var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
            if (group.Key == document.Id)
            {
                replacements[methodDecl] = newMethod;
                sourceReplaced = true;
            }

            foreach (var site in group)
            {
                var replacement = BuildCallSiteReplacement(site, needsRename, @params.UpdateCallers, newMethodName);
                if (replacement != null)
                    replacements[replacement.Original] = replacement.Replacement;
            }

            if (replacements.Count == 0) continue;

            var newRefRoot = refRoot.ReplaceNodes(replacements.Keys, (old, _) => replacements[old]);
            newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
        }

        if (!sourceReplaced)
        {
            var currentDoc = newSolution.GetDocument(document.Id) ?? document;
            var currentRoot = await currentDoc.GetSyntaxRootAsync(cancellationToken) ?? root;
            var newRoot = currentRoot.ReplaceNode(methodDecl, newMethod);
            newSolution = currentDoc.WithSyntaxRoot(newRoot).Project.Solution;
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = newMethodName,
                FullyQualifiedName = $"{methodSymbol.ContainingType.ToDisplayString()}.{newMethodName}",
                Kind = Contracts.Enums.SymbolKind.Method
            },
            ReferencesUpdated = awaitableCalls.Count,
            CallersUpdated = plan.Updated.Count,
            CallersSkipped = plan.Skipped.Count == 0 ? null : plan.Skipped
        };
    }

    /// <summary>
    /// Converts every distinct eligible sync method in every C# document
    /// (same document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>ConvertPropertyOperation.ExecuteAllFilesAsync</c> /
    /// <c>InvertIfOperation.ExecuteAllFilesAsync</c>:
    /// <c>FilePath</c> ends with <c>.cs</c>). Already-async, iterator,
    /// unresolved, override, and otherwise unsupported methods or documents
    /// whose text is unchanged are skipped. Project entry-point /
    /// traditional static <c>Main</c> is converted in place (not renamed
    /// to <c>MainAsync</c>) so the console entry point stays valid.
    /// When every file is a no-op, succeeds with empty changes.
    /// <see cref="ConvertToAsyncParams.RenameToAsync"/> (default true) and
    /// <see cref="ConvertToAsyncParams.UpdateCallers"/> (default false)
    /// apply across the walk using the existing await-wrap rules.
    /// Call-site identifier rewrites for converted methods are applied
    /// (including method-group / nameof references); already-async callers
    /// are awaited only when <c>updateCallers</c> is true.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ConvertToAsyncParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = new List<ConversionCandidate>();
        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (document is SourceGeneratedDocument)
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || semanticModel == null)
                continue;

            var compilation = await document.Project.GetCompilationAsync(cancellationToken);
            var entryPoint = compilation?.GetEntryPoint(cancellationToken);

            foreach (var method in CollectEligibleMethods(root, semanticModel, cancellationToken))
            {
                var symbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
                if (symbol == null)
                    continue;

                var methodName = method.Identifier.Text;
                candidates.Add(new ConversionCandidate(
                    document.Id,
                    method,
                    symbol,
                    FindAwaitableCalls(method, semanticModel, cancellationToken),
                    methodName,
                    ResolveAsyncMethodNameForAllFiles(
                        symbol, methodName, @params.RenameToAsync, entryPoint)));
            }
        }

        var convertedMethods = candidates.Select(c => c.Method).ToList();
        var allCallSites = new List<(ConversionCandidate Candidate, CallSite Site)>();
        foreach (var candidate in candidates)
        {
            var document = originalSolution.GetDocument(candidate.DocumentId);
            if (document == null)
                continue;

            var needsRename = candidate.NewMethodName != candidate.MethodName;
            if (!needsRename && !@params.UpdateCallers)
                continue;

            var callSites = await CollectCallSitesAsync(
                candidate.Symbol,
                candidate.Method,
                document,
                candidate.MethodName,
                cancellationToken);
            foreach (var site in callSites)
                allCallSites.Add((candidate, site));
        }

        var convertedCountByDoc = candidates
            .GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.Count());

        var documentsToRewrite = allDocuments
            .Select(d => d.Id)
            .Where(id =>
                convertedCountByDoc.ContainsKey(id) ||
                allCallSites.Any(s => s.Site.DocumentId == id))
            .Distinct()
            .ToList();

        var currentSolution = originalSolution;
        var anyChanged = false;
        var allPendingChanges = new List<PendingChange>();

        foreach (var documentId in documentsToRewrite)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalDocument = originalSolution.GetDocument(documentId);
            if (originalDocument == null || originalDocument is SourceGeneratedDocument)
                continue;

            var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
            if (originalRoot == null)
                continue;

            var newRoot = RewriteDocumentForAllFiles(
                originalRoot,
                documentId,
                candidates,
                convertedMethods,
                allCallSites,
                @params.UpdateCallers);

            var currentDocument = currentSolution.GetDocument(documentId) ?? originalDocument;
            var newDocument = currentDocument.WithSyntaxRoot(newRoot);
            var beforeText = await originalDocument.GetTextAsync(cancellationToken);
            var afterText = await newDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var span = originalRoot.GetLocation().GetLineSpan();
                var convertedCount = convertedCountByDoc.GetValueOrDefault(documentId);
                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = convertedCount > 0
                        ? BuildAllFilesDescription(convertedCount)
                        : "Update call sites of converted methods",
                    BeforeSnippet = originalRoot.NormalizeWhitespace().ToFullString().Trim(),
                    AfterSnippet = newRoot.NormalizeWhitespace().ToFullString().Trim(),
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                });
                continue;
            }

            currentSolution = newDocument.Project.Solution;
            anyChanged = true;
        }

        if (@params.Preview)
            return RefactoringResult.PreviewResult(operationId, allPendingChanges);

        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
            return RefactoringResult.Succeeded(operationId,
                new FileChanges
                {
                    FilesModified = commitResult.FilesModified,
                    FilesCreated = commitResult.FilesCreated,
                    FilesDeleted = commitResult.FilesDeleted
                },
                null, 0, 0);
        }

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
            null, 0, 0);
    }

    internal static string BuildAllFilesDescription(int convertedCount) =>
        convertedCount == 1
            ? "Convert method to async"
            : $"Convert {convertedCount} methods to async";

    /// <summary>
    /// Collects every distinct eligible sync <see cref="MethodDeclarationSyntax"/>
    /// in <paramref name="root"/> using the same already-async / iterator /
    /// unresolved / no-awaitable-call eligibility as the single-site path
    /// (skip, not throw), plus allFiles-only skips for <c>override</c>
    /// methods whose signature cannot change without breaking the base
    /// contract.
    /// </summary>
    internal static IReadOnlyList<MethodDeclarationSyntax> CollectEligibleMethods(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => TryGetEligibleMethod(method, semanticModel, cancellationToken, out _, out _))
            .ToList();

    /// <summary>
    /// Classifies a method for allFiles: already-async, iterator, unresolved,
    /// override, and methods with no awaitable calls are ineligible.
    /// </summary>
    internal static bool TryGetEligibleMethod(
        MethodDeclarationSyntax methodDecl,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out IMethodSymbol? methodSymbol,
        out List<InvocationExpressionSyntax> awaitableCalls)
    {
        methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        awaitableCalls = [];

        if (methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword))
            return false;

        if (methodDecl.DescendantNodes().OfType<YieldStatementSyntax>().Any())
            return false;

        if (methodDecl.Modifiers.Any(SyntaxKind.OverrideKeyword))
            return false;

        if (methodSymbol == null)
            return false;

        // Changing an override's name or return type cannot preserve the
        // base signature (CS0115 / unimplemented virtuals).
        if (methodSymbol.IsOverride)
            return false;

        awaitableCalls = FindAwaitableCalls(methodDecl, semanticModel, cancellationToken);
        return awaitableCalls.Count > 0;
    }

    internal static string ResolveAsyncMethodName(string methodName, bool renameToAsync) =>
        renameToAsync && !methodName.EndsWith("Async", StringComparison.Ordinal)
            ? methodName + "Async"
            : methodName;

    /// <summary>
    /// allFiles never renames the compilation entry point or a traditional
    /// static <c>Main</c> — C# only recognizes <c>Main</c> as the console
    /// entry-point name. Single-site rename is unchanged.
    /// </summary>
    internal static string ResolveAsyncMethodNameForAllFiles(
        IMethodSymbol symbol,
        string methodName,
        bool renameToAsync,
        IMethodSymbol? compilationEntryPoint) =>
        IsPreservedEntryPoint(symbol, compilationEntryPoint)
            ? methodName
            : ResolveAsyncMethodName(methodName, renameToAsync);

    internal static bool IsPreservedEntryPoint(
        IMethodSymbol symbol,
        IMethodSymbol? compilationEntryPoint)
    {
        if (compilationEntryPoint != null &&
            SymbolEqualityComparer.Default.Equals(symbol, compilationEntryPoint))
        {
            return true;
        }

        return symbol.Name == "Main" && symbol.IsStatic;
    }

    private static SyntaxNode RewriteDocumentForAllFiles(
        SyntaxNode root,
        DocumentId documentId,
        IReadOnlyList<ConversionCandidate> candidates,
        IReadOnlyList<MethodDeclarationSyntax> convertedMethods,
        IReadOnlyList<(ConversionCandidate Candidate, CallSite Site)> allCallSites,
        bool updateCallers)
    {
        var methodReplacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var callSiteActions = new Dictionary<SyntaxNode, (CallSite Site, ConversionCandidate Candidate)>();
        var methodsInDocument = candidates.Where(c => c.DocumentId == documentId).ToList();

        foreach (var candidate in methodsInDocument)
        {
            var annotatedNodes = new Dictionary<SyntaxNode, List<SyntaxAnnotation>>();
            foreach (var call in candidate.AwaitableCalls)
                AddAnnotation(annotatedNodes, call, AwaitableCallAnnotation);

            foreach (var (siteCandidate, site) in allCallSites)
            {
                if (site.DocumentId != documentId ||
                    !candidate.Method.Span.Contains(site.Name.Span))
                {
                    continue;
                }

                var needsRename = siteCandidate.NewMethodName != siteCandidate.MethodName;
                if (site.Invocation != null)
                {
                    if (needsRename)
                        AddAnnotation(annotatedNodes, site.Invocation, RenameAnnotation(siteCandidate.NewMethodName));

                    if (ShouldAwaitAllFilesCallSite(site, updateCallers, convertedMethods))
                        AddAnnotation(annotatedNodes, site.Invocation, SelfCallAwaitAnnotation);
                }
                else if (needsRename)
                {
                    // Method-group / nameof / other non-invocation references.
                    // Mirror single-site BuildCallSiteReplacement (Invocation == null).
                    AddAnnotation(annotatedNodes, site.Name, RenameAnnotation(siteCandidate.NewMethodName));
                }
            }

            foreach (var (name, newName) in FindNameOfReferencesToConvertedMethods(
                candidate.Method, candidates))
            {
                AddAnnotation(annotatedNodes, name, RenameAnnotation(newName));
            }

            var methodToRewrite = candidate.Method;
            if (annotatedNodes.Count > 0)
            {
                methodToRewrite = candidate.Method.ReplaceNodes(
                    annotatedNodes.Keys,
                    (original, _) => original.WithAdditionalAnnotations(annotatedNodes[original]));
            }

            methodToRewrite = (MethodDeclarationSyntax)new AnnotatedCallRewriter(
                candidate.MethodName,
                candidate.NewMethodName).Visit(methodToRewrite)!;

            methodReplacements[candidate.Method] = methodToRewrite
                .WithIdentifier(SyntaxFactory.Identifier(candidate.NewMethodName))
                .WithReturnType(SyntaxGenerationHelper.ToAsyncReturnType(candidate.Symbol.ReturnType)
                    .WithTrailingTrivia(SyntaxFactory.Space))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        }

        foreach (var (siteCandidate, site) in allCallSites)
        {
            if (site.DocumentId != documentId)
                continue;

            if (IsInsideAnyConvertedMethod(site, convertedMethods))
                continue;

            var original = GetCallSiteReplacementOriginal(site, updateCallers);
            if (original == null)
                continue;

            callSiteActions[original] = (site, siteCandidate);
        }

        var nodes = methodReplacements.Keys
            .Concat(callSiteActions.Keys)
            .Distinct()
            .ToList();
        if (nodes.Count == 0)
            return root;

        // Compose from the rewritten descendant so nested A(B()) keeps the
        // inner rename/await when the outer invocation is replaced.
        return root.ReplaceNodes(nodes, (old, rewritten) =>
        {
            if (methodReplacements.TryGetValue(old, out var newMethod))
                return newMethod;

            if (callSiteActions.TryGetValue(old, out var action))
                return ApplyCallSiteReplacementToRewritten(rewritten, action.Site, action.Candidate, updateCallers);

            return rewritten;
        });
    }

    private static SyntaxNode? GetCallSiteReplacementOriginal(CallSite site, bool updateCallers)
    {
        if (site.Invocation == null)
            return site.Name;

        if (ShouldAwaitCallSite(site, updateCallers))
            return GetAwaitWrapTarget(site.Invocation);

        return site.Name;
    }

    /// <summary>
    /// Applies the same rename / await-wrap as <see cref="BuildCallSiteReplacement"/>
    /// to a node that may already contain rewritten descendants.
    /// </summary>
    private static SyntaxNode ApplyCallSiteReplacementToRewritten(
        SyntaxNode rewritten,
        CallSite site,
        ConversionCandidate candidate,
        bool updateCallers)
    {
        var needsRename = candidate.NewMethodName != candidate.MethodName;
        if (rewritten is SimpleNameSyntax simpleName)
        {
            return needsRename
                ? WithMethodName(simpleName, candidate.NewMethodName)
                : rewritten;
        }

        if (rewritten is not ExpressionSyntax expression)
            return rewritten;

        var updated = expression;
        if (needsRename)
        {
            var name = FindNameNode(updated, candidate.MethodName)
                ?? FindInvocationName(updated as InvocationExpressionSyntax);
            if (name != null && name.Identifier.Text != candidate.NewMethodName)
                updated = (ExpressionSyntax)updated.ReplaceNode(name, WithMethodName(name, candidate.NewMethodName));
        }

        if (ShouldAwaitCallSite(site, updateCallers) && !IsAlreadyAwaited(updated))
            return WrapWithAwait(updated);

        return updated;
    }

    private static IEnumerable<(SimpleNameSyntax Name, string NewName)> FindNameOfReferencesToConvertedMethods(
        MethodDeclarationSyntax method,
        IReadOnlyList<ConversionCandidate> candidates)
    {
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not IdentifierNameSyntax { Identifier.Text: "nameof" })
                continue;

            if (invocation.ArgumentList.Arguments.Count != 1)
                continue;

            if (invocation.ArgumentList.Arguments[0].Expression is not SimpleNameSyntax name)
                continue;

            var match = candidates.FirstOrDefault(c =>
                c.MethodName == name.Identifier.Text &&
                c.NewMethodName != c.MethodName);
            if (match != null)
                yield return (name, match.NewMethodName);
        }
    }

    private static SimpleNameSyntax? FindInvocationName(InvocationExpressionSyntax? invocation) =>
        invocation?.Expression switch
        {
            SimpleNameSyntax simple => simple,
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            _ => null
        };

    internal static bool ShouldAwaitAllFilesCallSite(
        CallSite site,
        bool updateCallers,
        IReadOnlyCollection<MethodDeclarationSyntax> convertedMethods)
    {
        if (!updateCallers || site.Invocation == null || site.IsAlreadyAwaited)
            return false;

        if (site.IsAsyncContext)
            return true;

        return convertedMethods.Any(method =>
            method.SyntaxTree == site.Name.SyntaxTree &&
            IsNearestEnclosingConvertedMethod(site.Name, method));
    }

    private static bool IsInsideAnyConvertedMethod(
        CallSite site,
        IReadOnlyCollection<MethodDeclarationSyntax> convertedMethods) =>
        convertedMethods.Any(method =>
            method.SyntaxTree == site.Name.SyntaxTree &&
            method.Span.Contains(site.Name.Span));

    private static SyntaxAnnotation RenameAnnotation(string newName) =>
        new(ConvertedCallRenameKind, newName);

    /// <summary>
    /// Finds a method by name. When <paramref name="column"/> is omitted,
    /// keeps today's first-match (MethodName; Line when more than one
    /// match, start-line filter). When set, picks the method whose
    /// identifier or declaration span covers that 1-based column.
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
            // column.
            return methods
                .Where(m => MethodCoversColumn(m, line ?? StartLine(m), column.Value))
                .OrderBy(m => IdentifierCoversColumn(m, line ?? StartLine(m), column.Value) ? 0 : 1)
                .ThenBy(m => m.Span.Length)
                .FirstOrDefault();
        }

        // Omitted column keeps today's MethodName + Line pick: a single
        // name match is used as-is (Line is only for disambiguation).
        // More than one match uses the first whose declaration starts on
        // `line`.
        if (methods.Count <= 1)
            return methods.FirstOrDefault();

        if (!line.HasValue)
            return methods.FirstOrDefault();

        return methods.FirstOrDefault(m => StartLine(m) == line.Value);
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
    /// match the previous declaration.
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

    private static async Task<List<CallSite>> CollectCallSitesAsync(
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodDecl,
        Document document,
        string methodName,
        CancellationToken cancellationToken)
    {
        var solution = document.Project.Solution;
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol,
            solution,
            cancellationToken);

        var declarationNameSpan = methodDecl.Identifier.Span;
        var callSites = new List<CallSite>();

        foreach (var location in references.SelectMany(reference => reference.Locations))
        {
            var refDoc = solution.GetDocument(location.Document.Id);
            if (refDoc == null) continue;

            var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
            if (refRoot == null) continue;

            var node = refRoot.FindNode(location.Location.SourceSpan);
            var name = FindNameNode(node, methodName);
            if (name == null) continue;

            var isDeclarationName = location.Document.Id == document.Id
                && declarationNameSpan.Contains(location.Location.SourceSpan);
            if (isDeclarationName) continue;

            var invocation = GetContainingInvocation(name);
            var (isAsync, enclosingName) = GetEnclosingCallable(name);
            var isInsideConvertedMethod = location.Document.Id == document.Id
                && methodDecl.Span.Contains(name.Span);
            var isEnclosingConvertedMethod = location.Document.Id == document.Id
                && IsNearestEnclosingConvertedMethod(name, methodDecl);

            callSites.Add(new CallSite(
                location.Document.Id,
                refDoc.FilePath ?? location.Document.Name,
                name,
                invocation,
                isInsideConvertedMethod,
                isEnclosingConvertedMethod,
                isAsync,
                invocation != null && IsAlreadyAwaited(invocation),
                enclosingName));
        }

        return callSites;
    }

    internal static CallerUpdatePlan PlanCallerUpdates(IReadOnlyList<CallSite> callSites, bool updateCallers)
    {
        var updated = new List<CallSite>();
        var skipped = new List<SkippedCaller>();

        if (!updateCallers)
            return new CallerUpdatePlan(updated, skipped);

        foreach (var site in callSites)
        {
            if (site.Invocation == null)
            {
                skipped.Add(new SkippedCaller
                {
                    Caller = site.EnclosingName,
                    Reason = NotInvocationSkipReason
                });
                continue;
            }

            if (site.IsAlreadyAwaited)
                continue;

            // Await only when the nearest enclosing callable is already async,
            // or is the converted method itself (it becomes async). Nested sync
            // local functions / lambdas stay unawaited even when they live inside
            // the converted method.
            if (ShouldAwaitCallSite(site, updateCallers: true))
            {
                updated.Add(site);
                continue;
            }

            skipped.Add(new SkippedCaller
            {
                Caller = site.EnclosingName,
                Reason = SyncCallerSkipReason
            });
        }

        return new CallerUpdatePlan(updated, skipped);
    }

    private static NodeReplacement? BuildCallSiteReplacement(
        CallSite site,
        bool needsRename,
        bool updateCallers,
        string newMethodName)
    {
        if (site.Invocation == null)
        {
            if (needsRename)
                return new NodeReplacement(site.Name, WithMethodName(site.Name, newMethodName));
            return null;
        }

        var invocation = site.Invocation;
        ExpressionSyntax updatedInvocation = invocation;
        if (needsRename)
        {
            var renamed = WithMethodName(site.Name, newMethodName);
            updatedInvocation = invocation.ReplaceNode(site.Name, renamed);
        }

        if (ShouldAwaitCallSite(site, updateCallers))
        {
            var wrapTarget = GetAwaitWrapTarget(invocation);
            if (wrapTarget is ConditionalAccessExpressionSyntax conditional)
            {
                var updatedConditional = conditional.ReplaceNode(invocation, updatedInvocation);
                return new NodeReplacement(conditional, WrapWithAwait(updatedConditional));
            }

            return new NodeReplacement(invocation, WrapWithAwait(updatedInvocation));
        }

        if (needsRename)
            return new NodeReplacement(site.Name, WithMethodName(site.Name, newMethodName));

        return null;
    }

    internal static SimpleNameSyntax? FindNameNode(SyntaxNode node, string methodName)
    {
        if (node is SimpleNameSyntax simple && simple.Identifier.Text == methodName)
            return simple;

        return node.DescendantNodesAndSelf()
            .OfType<SimpleNameSyntax>()
            .FirstOrDefault(name => name.Identifier.Text == methodName);
    }

    internal static InvocationExpressionSyntax? GetContainingInvocation(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax direct && direct.Expression == name)
            return direct;

        if (name.Parent is MemberAccessExpressionSyntax member
            && member.Name == name
            && member.Parent is InvocationExpressionSyntax memberInvocation
            && memberInvocation.Expression == member)
        {
            return memberInvocation;
        }

        if (name.Parent is MemberBindingExpressionSyntax binding
            && binding.Name == name
            && binding.Parent is InvocationExpressionSyntax bindingInvocation
            && bindingInvocation.Expression == binding)
        {
            return bindingInvocation;
        }

        return null;
    }

    /// <summary>
    /// Await when the nearest enclosing callable is already async, or is the
    /// converted method (it becomes async). Nested sync local functions and
    /// lambdas are not awaited.
    /// </summary>
    internal static bool ShouldAwaitCallSite(CallSite site, bool updateCallers)
    {
        if (!updateCallers || site.Invocation == null || site.IsAlreadyAwaited)
            return false;

        return site.IsAsyncContext || site.IsEnclosingConvertedMethod;
    }

    /// <summary>
    /// True when the nearest enclosing callable is <paramref name="methodDecl"/>
    /// (no nested local function, lambda, or other callable in between).
    /// </summary>
    internal static bool IsNearestEnclosingConvertedMethod(SyntaxNode node, MethodDeclarationSyntax methodDecl)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    return method == methodDecl;
                case LocalFunctionStatementSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case SimpleLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                case ConstructorDeclarationSyntax:
                case DestructorDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                case AccessorDeclarationSyntax:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Conditional access invocations (<c>obj?.M()</c>) must be awaited at the
    /// outermost <c>?.</c> expression, not the inner WhenNotNull invocation.
    /// </summary>
    internal static ExpressionSyntax GetAwaitWrapTarget(ExpressionSyntax expression)
    {
        var current = expression;
        while (current.Parent is ConditionalAccessExpressionSyntax conditional
               && conditional.WhenNotNull == current)
        {
            current = conditional;
        }

        return current;
    }

    internal static (bool IsAsync, string Name) GetEnclosingCallable(SyntaxNode node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    return (method.Modifiers.Any(SyntaxKind.AsyncKeyword), method.Identifier.Text);
                case LocalFunctionStatementSyntax local:
                    return (local.Modifiers.Any(SyntaxKind.AsyncKeyword), local.Identifier.Text);
                case ParenthesizedLambdaExpressionSyntax paren:
                    return (paren.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword), "(lambda)");
                case SimpleLambdaExpressionSyntax simple:
                    return (simple.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword), "(lambda)");
                case AnonymousMethodExpressionSyntax anon:
                    return (anon.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword), "(anonymous method)");
                case ConstructorDeclarationSyntax ctor:
                    return (false, ctor.Identifier.Text);
                case DestructorDeclarationSyntax:
                    return (false, "destructor");
                case OperatorDeclarationSyntax op:
                    return (false, op.OperatorToken.Text);
                case ConversionOperatorDeclarationSyntax:
                    return (false, "conversion operator");
                case AccessorDeclarationSyntax accessor:
                    return (false, accessor.Keyword.Text);
            }
        }

        return (false, "(unknown)");
    }

    internal static bool IsAlreadyAwaited(ExpressionSyntax expression)
    {
        var parent = GetAwaitWrapTarget(expression).Parent;
        while (parent is ParenthesizedExpressionSyntax paren)
            parent = paren.Parent;

        return parent is AwaitExpressionSyntax;
    }

    internal static bool NeedsParentheses(ExpressionSyntax expression)
    {
        return expression.Parent is MemberAccessExpressionSyntax
            or ConditionalAccessExpressionSyntax
            or ElementAccessExpressionSyntax
            or InvocationExpressionSyntax
            or PostfixUnaryExpressionSyntax;
    }

    internal static SimpleNameSyntax WithMethodName(SimpleNameSyntax name, string newName)
    {
        return name.WithIdentifier(SyntaxFactory.Identifier(newName).WithTriviaFrom(name.Identifier));
    }

    internal static ExpressionSyntax WrapWithAwait(ExpressionSyntax expression)
    {
        var awaitExpression = SyntaxFactory.AwaitExpression(
            SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            expression.WithoutLeadingTrivia());

        if (NeedsParentheses(expression))
        {
            return SyntaxFactory.ParenthesizedExpression(awaitExpression.WithoutTrivia())
                .WithTriviaFrom(expression);
        }

        return awaitExpression.WithLeadingTrivia(expression.GetLeadingTrivia());
    }

    internal static string DescribeCallerUpdates(bool updateCallers, CallerUpdatePlan plan)
    {
        if (!updateCallers)
            return "Callers will not be updated to await the converted method.";

        if (plan.Updated.Count == 0 && plan.Skipped.Count == 0)
            return "No callers will be updated to await the converted method.";

        var parts = new List<string>();
        if (plan.Updated.Count == 1)
            parts.Add("Update 1 caller to await the converted method.");
        else if (plan.Updated.Count > 1)
            parts.Add($"Update {plan.Updated.Count} callers to await the converted method.");

        if (plan.Skipped.Count == 1)
            parts.Add("Skip 1 caller that cannot legally await.");
        else if (plan.Skipped.Count > 1)
            parts.Add($"Skip {plan.Skipped.Count} callers that cannot legally await.");

        if (parts.Count == 0)
            return "No callers will be updated to await the converted method.";

        return string.Join(" ", parts);
    }

    private static List<InvocationExpressionSyntax> FindAwaitableCalls(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var awaitableCalls = new List<InvocationExpressionSyntax>();

        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            if (methodInfo.Symbol is not IMethodSymbol calledMethod) continue;

            // Check if return type is awaitable (Task, Task<T>, ValueTask, ValueTask<T>)
            var returnType = calledMethod.ReturnType;
            if (IsAwaitableType(returnType))
            {
                awaitableCalls.Add(invocation);
            }
        }

        return awaitableCalls;
    }

    private static bool IsAwaitableType(ITypeSymbol type)
    {
        var typeName = type.ToDisplayString();
        return typeName.StartsWith("System.Threading.Tasks.Task") ||
               typeName.StartsWith("System.Threading.Tasks.ValueTask") ||
               typeName.StartsWith("Task<") ||
               typeName.StartsWith("ValueTask<") ||
               typeName == "Task" ||
               typeName == "ValueTask";
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ConvertToAsyncParams @params,
        IMethodSymbol method,
        string newMethodName,
        int awaitableCallCount,
        CallerUpdatePlan plan)
    {
        var oldSig = $"{method.ReturnType.ToDisplayString()} {@params.MethodName}(...)";
        var newReturnType = method.ReturnsVoid ? "Task" : $"Task<{method.ReturnType.ToDisplayString()}>";
        var newSig = $"async {newReturnType} {newMethodName}(...)";
        var callerDescription = DescribeCallerUpdates(@params.UpdateCallers, plan);

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = $"Convert '{@params.MethodName}' to async ({awaitableCallCount} await expressions added)",
                BeforeSnippet = oldSig,
                AfterSnippet = newSig
            },
            new()
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = callerDescription
            }
        };

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Preview = true,
            PendingChanges = pendingChanges,
            CallersUpdated = plan.Updated.Count,
            CallersSkipped = plan.Skipped.Count == 0 ? null : plan.Skipped
        };
    }

    internal sealed record CallSite(
        DocumentId DocumentId,
        string FilePath,
        SimpleNameSyntax Name,
        InvocationExpressionSyntax? Invocation,
        bool IsInsideConvertedMethod,
        bool IsEnclosingConvertedMethod,
        bool IsAsyncContext,
        bool IsAlreadyAwaited,
        string EnclosingName);

    internal sealed record CallerUpdatePlan(
        IReadOnlyList<CallSite> Updated,
        IReadOnlyList<SkippedCaller> Skipped);

    private sealed record NodeReplacement(SyntaxNode Original, SyntaxNode Replacement);

    private static void AddAnnotation(
        Dictionary<SyntaxNode, List<SyntaxAnnotation>> map,
        SyntaxNode node,
        SyntaxAnnotation annotation)
    {
        if (!map.TryGetValue(node, out var annotations))
        {
            annotations = [];
            map[node] = annotations;
        }

        annotations.Add(annotation);
    }

    private static void AddAnnotation(
        Dictionary<InvocationExpressionSyntax, List<SyntaxAnnotation>> map,
        InvocationExpressionSyntax invocation,
        SyntaxAnnotation annotation)
    {
        if (!map.TryGetValue(invocation, out var annotations))
        {
            annotations = [];
            map[invocation] = annotations;
        }

        annotations.Add(annotation);
    }

    private sealed class AnnotatedCallRewriter : CSharpSyntaxRewriter
    {
        private readonly string _oldName;
        private readonly string _newName;

        public AnnotatedCallRewriter(string oldName, string newName)
        {
            _oldName = oldName;
            _newName = newName;
        }

        public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            var visited = (ConditionalAccessExpressionSyntax)base.VisitConditionalAccessExpression(node)!;
            var invocation = FindAwaitableInvocationUnder(node);
            if (invocation != null && GetAwaitWrapTarget(invocation) == node)
                return WrapWithAwait(visited);

            return visited;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            var isSelfCall = node.HasAnnotation(SelfCallAnnotation);
            var renameTo = node.GetAnnotations(ConvertedCallRenameKind).FirstOrDefault()?.Data;

            if (renameTo != null)
            {
                var name = GetInvocationName(visited) ?? FindNameNode(visited, _oldName);
                if (name != null && name.Identifier.Text != renameTo)
                    visited = visited.ReplaceNode(name, WithMethodName(name, renameTo));
            }
            else if (isSelfCall && _oldName != _newName)
            {
                var name = FindNameNode(visited, _oldName);
                if (name != null)
                    visited = visited.ReplaceNode(name, WithMethodName(name, _newName));
            }

            if (ShouldAwaitAnnotated(node) && GetAwaitWrapTarget(node) == node)
            {
                return SyntaxFactory.AwaitExpression(
                    SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    visited.WithoutLeadingTrivia())
                    .WithLeadingTrivia(visited.GetLeadingTrivia());
            }

            return visited;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var visited = (IdentifierNameSyntax)base.VisitIdentifierName(node)!;
            return RenameAnnotatedName(node, visited);
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
            return RenameAnnotatedName(node, visited);
        }

        private static SimpleNameSyntax RenameAnnotatedName(SimpleNameSyntax original, SimpleNameSyntax visited)
        {
            var renameTo = original.GetAnnotations(ConvertedCallRenameKind).FirstOrDefault()?.Data;
            if (renameTo != null && visited.Identifier.Text != renameTo)
                return WithMethodName(visited, renameTo);

            return visited;
        }

        private static bool ShouldAwaitAnnotated(InvocationExpressionSyntax node)
        {
            return node.HasAnnotation(AwaitableCallAnnotation)
                || node.HasAnnotation(SelfCallAwaitAnnotation);
        }

        private static InvocationExpressionSyntax? FindAwaitableInvocationUnder(
            ConditionalAccessExpressionSyntax node)
        {
            return node.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(ShouldAwaitAnnotated);
        }

        private static SimpleNameSyntax? GetInvocationName(InvocationExpressionSyntax invocation) =>
            invocation.Expression switch
            {
                SimpleNameSyntax simple => simple,
                MemberAccessExpressionSyntax member => member.Name,
                MemberBindingExpressionSyntax binding => binding.Name,
                _ => null
            };
    }

    private sealed record ConversionCandidate(
        DocumentId DocumentId,
        MethodDeclarationSyntax Method,
        IMethodSymbol Symbol,
        List<InvocationExpressionSyntax> AwaitableCalls,
        string MethodName,
        string NewMethodName);
}

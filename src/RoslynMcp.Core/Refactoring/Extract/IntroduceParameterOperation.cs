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
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Promotes a local variable to a method parameter and updates call sites.
/// Honors optional <c>column</c> to disambiguate same-line multi-declarators
/// and continuation-line identifiers (identifier preferred, then smallest
/// covering declarator). Omitted column keeps today's start-line equality
/// on the local declaration statement, then <c>variableName</c>
/// <c>FirstOrDefault</c>. Do not force column 1 when omitted. Do not
/// rewrite line-only to covering-span. After the rewrite, keep the
/// selected declarator — do not rematch by name, stale SpanStart, or line.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and promotes every eligible local, skipping ineligible
/// locals rather than throwing.
/// </summary>
public sealed class IntroduceParameterOperation : RefactoringOperationBase<IntroduceParameterParams>
{
    /// <inheritdoc />
    public IntroduceParameterOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(IntroduceParameterParams @params) => Validate(@params);

    /// <summary>
    /// Validates introduce-parameter parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(IntroduceParameterParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.VariableName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with variableName, line, or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!@params.Line.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "line is required.");

        var sourceFile = @params.SourceFile!;

        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        IntroduceParameterParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var variableName = @params.VariableName!;
        var line = @params.Line!.Value;

        var document = GetDocumentOrThrow(sourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Find the local variable declaration. Omitted column keeps
        // today's start-line + name FirstOrDefault. Column set picks the
        // covering declarator (identifier preferred, then smallest).
        var localDecl = FindLocalDeclarator(root, variableName, line, @params.Column);

        if (localDecl == null)
        {
            var location = @params.Column.HasValue
                ? $"line {line}, column {@params.Column.Value}"
                : $"line {line}";
            throw new RefactoringException(ErrorCodes.SymbolNotFound,
                $"Local variable '{variableName}' not found at {location}.");
        }

        // Get the containing method
        var containingMethod = localDecl.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null)
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "Local variable must be inside a method to be promoted to a parameter.");
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken);
        if (methodSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        // Get the variable's type
        var localDeclStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var declaredType = localDeclStatement.Declaration.Type;

        // If using 'var', resolve the actual type
        TypeSyntax paramType;
        if (declaredType.IsVar)
        {
            var typeInfo = semanticModel.GetTypeInfo(declaredType, cancellationToken);
            if (typeInfo.Type != null)
            {
                paramType = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
            }
            else
            {
                paramType = declaredType;
            }
        }
        else
        {
            paramType = declaredType;
        }

        // Get the initializer expression (used as default value at call sites)
        var initializer = localDecl.Initializer?.Value;

        if (@params.Preview)
        {
            var beforeSig = containingMethod.Identifier.Text + "(" +
                            string.Join(", ", containingMethod.ParameterList.Parameters.Select(p => p.ToString())) + ")";
            var afterSig = containingMethod.Identifier.Text + "(" +
                           string.Join(", ", containingMethod.ParameterList.Parameters.Select(p => p.ToString())) +
                           (containingMethod.ParameterList.Parameters.Count > 0 ? ", " : "") +
                           $"{paramType.NormalizeWhitespace()} {variableName})";

            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = sourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Promote '{variableName}' to parameter of '{containingMethod.Identifier.Text}'",
                    BeforeSnippet = beforeSig,
                    AfterSnippet = afterSig
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newSolution = await ApplyPromoteAsync(
            document,
            root,
            localDecl,
            containingMethod,
            methodSymbol,
            paramType,
            variableName,
            initializer,
            cancellationToken);

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = containingMethod.Identifier.Text, FullyQualifiedName = $"{methodSymbol.ContainingType.ToDisplayString()}.{containingMethod.Identifier.Text}", Kind = Contracts.Enums.SymbolKind.Method },
            0, 0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>InlineConstantOperation.ExecuteAllFilesAsync</c>
    /// / <c>InlineVariableOperation.ExecuteAllFilesAsync</c>) and promotes
    /// every eligible local <see cref="VariableDeclaratorSyntax"/> in a
    /// <see cref="LocalDeclarationStatementSyntax"/> inside a method body.
    /// Optional <c>sourceFile</c> limits via <see cref="DocumentSourceFileFilter"/>.
    /// Linked documents that share a physical path are rewritten once and the
    /// same text is applied to every sibling <see cref="DocumentId"/> via
    /// <see cref="Solution.GetChanges(Solution)"/> coalesce (prefer a changed
    /// DocumentId as source). Ineligible locals (not inside a method body,
    /// inside a local function / anonymous function, override methods, ref /
    /// ref-readonly locals, methods with optional or params parameters, using
    /// declaration, no initializer, duplicate parameter name, uneditable /
    /// source-generated docs) are skipped rather than failing the walk.
    /// Deterministic <c>SpanStart</c> order within a file. When every file is
    /// a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        IntroduceParameterParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        // One physical path may appear as multiple Documents when linked into
        // several projects. Rewrite once per normalized path and apply the same
        // text to every sibling DocumentId (ConvertToBlockBody allFiles / Codex).
        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var promotedCountByDoc = new Dictionary<DocumentId, int>();

        foreach (var linkedDocuments in documentGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var primary = linkedDocuments.FirstOrDefault(d =>
                d is not SourceGeneratedDocument &&
                DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace));
            if (primary == null)
                continue;

            while (true)
            {
                var currentDocument = currentSolution.GetDocument(primary.Id);
                if (currentDocument == null ||
                    currentDocument is SourceGeneratedDocument ||
                    !DocumentEditableHelpers.IsDocumentEditable(currentDocument, Context.Workspace))
                {
                    break;
                }

                var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null)
                    break;

                Solution? updated = null;
                foreach (var declarator in CollectLocalDeclarators(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        updated = await TryPromoteOneAsync(
                            currentDocument,
                            root,
                            semanticModel,
                            declarator,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        // Skip ineligible locals rather than failing the walk.
                        updated = null;
                    }

                    if (updated != null)
                        break;
                }

                if (updated == null)
                    break;

                // Coalesce linked siblings for every physical path whose text
                // changed in this rewrite (declaring file and any call-site files).
                // Prefer a DocumentId that actually changed so an unchanged
                // sorted-first sibling cannot overwrite the rewrite (Codex/Copilot).
                var beforeSolution = currentSolution;
                currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                    beforeSolution,
                    updated,
                    Context.Workspace,
                    cancellationToken);

                promotedCountByDoc[primary.Id] =
                    promotedCountByDoc.GetValueOrDefault(primary.Id) + 1;
            }
        }

        var documentsToCompare = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;
        var previewedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documentsToCompare)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalDocument = originalSolution.GetDocument(document.Id);
            var currentDocument = currentSolution.GetDocument(document.Id);
            if (originalDocument == null || currentDocument == null)
                continue;

            var beforeText = await originalDocument.GetTextAsync(cancellationToken);
            var afterText = await currentDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var pathKey = PathResolver.GetPathComparisonKey(originalDocument.FilePath!);
                if (!previewedPaths.Add(pathKey))
                    continue;

                var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
                var currentRoot = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                if (originalRoot == null || currentRoot == null)
                    continue;

                var span = originalRoot.GetLocation().GetLineSpan();
                var promotedCount = promotedCountByDoc.GetValueOrDefault(document.Id);
                // Count may live on a sibling DocumentId in the same path group.
                if (promotedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        promotedCount = Math.Max(promotedCount, promotedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = promotedCount > 0
                        ? BuildAllFilesDescription(promotedCount)
                        : "Update call sites of promoted parameters",
                    BeforeSnippet = originalRoot.NormalizeWhitespace().ToFullString().Trim(),
                    AfterSnippet = currentRoot.NormalizeWhitespace().ToFullString().Trim(),
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                });
                continue;
            }

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

    /// <summary>
    /// Preview description for a file that promoted
    /// <paramref name="promotedCount"/> locals.
    /// </summary>
    internal static string BuildAllFilesDescription(int promotedCount) =>
        promotedCount == 1
            ? "Introduce parameter"
            : $"Introduce {promotedCount} parameters";

    /// <summary>
    /// Collects every local <see cref="VariableDeclaratorSyntax"/> in
    /// <paramref name="root"/> whose parent is a
    /// <see cref="LocalDeclarationStatementSyntax"/> (fields and for-loop
    /// declarators stay excluded). Deterministic <c>SpanStart</c> then
    /// span-length order.
    /// </summary>
    internal static IReadOnlyList<VariableDeclaratorSyntax> CollectLocalDeclarators(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(IsLocalDeclarator)
            .OrderBy(declarator => declarator.SpanStart)
            .ThenBy(declarator => declarator.Span.Length)
            .ToList();

    private static bool IsLocalDeclarator(VariableDeclaratorSyntax declarator) =>
        declarator.Parent is VariableDeclarationSyntax
        {
            Parent: LocalDeclarationStatementSyntax
        } &&
        !IsUsingDeclaration(declarator);

    private static bool IsUsingDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent switch
        {
            LocalDeclarationStatementSyntax statement => statement.UsingKeyword != default,
            UsingStatementSyntax => true,
            _ => false
        };

    private async Task<Solution?> TryPromoteOneAsync(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        VariableDeclaratorSyntax localDecl,
        CancellationToken cancellationToken)
    {
        if (localDecl.Initializer == null)
            return null;

        if (IsUsingDeclaration(localDecl))
            return null;

        // Prefer the nearest method; skip locals that live in a local function
        // or anonymous function (promoting onto the outer method would be incorrect).
        var nearestExecutable = localDecl.Ancestors()
            .FirstOrDefault(n =>
                n is MethodDeclarationSyntax
                    or LocalFunctionStatementSyntax
                    or AnonymousFunctionExpressionSyntax);
        if (nearestExecutable is not MethodDeclarationSyntax containingMethod)
            return null;

        if (containingMethod.Body == null)
            return null;

        var variableName = localDecl.Identifier.Text;
        if (containingMethod.ParameterList.Parameters.Any(p => p.Identifier.Text == variableName))
            return null;

        // Appending a required parameter after optional/params is illegal.
        if (containingMethod.ParameterList.Parameters.Any(p =>
                ParameterSyntaxHelpers.IsOptional(p) || ParameterSyntaxHelpers.IsParams(p)))
            return null;

        var methodSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken);
        if (methodSymbol == null)
            return null;

        // Bulk promote cannot rewrite the whole override hierarchy.
        if (methodSymbol.IsOverride)
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        // ApplyPromoteAsync always emits a by-value parameter.
        if (IsRefLocal(localDecl, semanticModel, cancellationToken))
            return null;

        var localDeclStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var declaredType = localDeclStatement.Declaration.Type;

        TypeSyntax paramType;
        if (declaredType.IsVar)
        {
            var typeInfo = semanticModel.GetTypeInfo(declaredType, cancellationToken);
            if (typeInfo.Type == null)
                return null;
            paramType = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
        }
        else
        {
            paramType = declaredType;
        }

        var initializer = localDecl.Initializer.Value;
        var beforeText = await document.GetTextAsync(cancellationToken);
        var newSolution = await ApplyPromoteAsync(
            document,
            root,
            localDecl,
            containingMethod,
            methodSymbol,
            paramType,
            variableName,
            initializer,
            cancellationToken);

        var afterDocument = newSolution.GetDocument(document.Id);
        if (afterDocument == null)
            return null;

        var afterText = await afterDocument.GetTextAsync(cancellationToken);
        if (beforeText.ContentEquals(afterText))
        {
            // Method text unchanged but call sites may have changed — keep
            // the solution if any document differs from the input.
            var anyDiff = false;
            foreach (var project in newSolution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    var originalDoc = document.Project.Solution.GetDocument(doc.Id);
                    if (originalDoc == null)
                        continue;
                    var before = await originalDoc.GetTextAsync(cancellationToken);
                    var after = await doc.GetTextAsync(cancellationToken);
                    if (!before.ContentEquals(after))
                    {
                        anyDiff = true;
                        break;
                    }
                }

                if (anyDiff)
                    break;
            }

            if (!anyDiff)
                return null;
        }

        return newSolution;
    }

    private static async Task<Solution> ApplyPromoteAsync(
        Document document,
        SyntaxNode root,
        VariableDeclaratorSyntax localDecl,
        MethodDeclarationSyntax containingMethod,
        IMethodSymbol methodSymbol,
        TypeSyntax paramType,
        string variableName,
        ExpressionSyntax? initializer,
        CancellationToken cancellationToken)
    {
        // Collect call sites against the pre-rewrite solution / symbols so
        // spans stay valid, including linked sibling compilations (Codex).
        var callSites = initializer == null
            ? Array.Empty<CallSite>()
            : await CollectCallSitesAsync(
                document,
                containingMethod,
                methodSymbol,
                document.Project.Solution,
                cancellationToken);

        // Resolve invocation nodes on the declaring document from the original
        // root (same-file sites must rewrite with the method in one
        // ReplaceNodes pass — post-edit spans shift).
        var declaringInvocations = new List<InvocationExpressionSyntax>();
        var otherCallSites = new List<CallSite>();
        foreach (var site in callSites)
        {
            if (site.DocumentId != document.Id)
            {
                otherCallSites.Add(site);
                continue;
            }

            var invocation = RematchInvocation(root, site.Span);
            if (invocation != null)
                declaringInvocations.Add(invocation);
        }

        // 1. Add parameter to method signature
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(variableName))
            .WithType(paramType.WithTrailingTrivia(SyntaxFactory.Space));

        var newParameterList = containingMethod.ParameterList.AddParameters(newParam);

        // 2. Remove the selected local declarator (do not rematch by name /
        // stale SpanStart / line after rewrite — keep today's selected node).
        // TrackNodes keeps the declarator identity across in-body call-site edits.
        var declarationStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var inBodyInvocations = declaringInvocations
            .Where(i => containingMethod.Body!.Span.Contains(i.Span))
            .Distinct()
            .ToList();

        var trackNodes = new List<SyntaxNode> { declarationStatement, localDecl };
        trackNodes.AddRange(inBodyInvocations);
        var newBody = containingMethod.Body!.TrackNodes(trackNodes);

        if (inBodyInvocations.Count > 0 && initializer != null)
        {
            foreach (var invocation in inBodyInvocations)
            {
                var current = newBody.GetCurrentNode(invocation);
                if (current == null)
                    continue;
                newBody = newBody.ReplaceNode(current, WithAddedArgument(current, initializer, variableName));
            }
        }

        var currentDeclaration = newBody.GetCurrentNode(declarationStatement)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Local declaration disappeared during rewrite.");
        var currentLocal = newBody.GetCurrentNode(localDecl)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Local declarator disappeared during rewrite.");

        if (currentDeclaration.Declaration.Variables.Count == 1)
        {
            // Remove entire statement
            newBody = newBody.RemoveNode(currentDeclaration, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            // Remove just this variable from the multi-variable declaration
            var newDeclaration = currentDeclaration.Declaration.RemoveNode(currentLocal, SyntaxRemoveOptions.KeepNoTrivia)!;
            var newDeclStatement = currentDeclaration.WithDeclaration(newDeclaration);
            newBody = (BlockSyntax)newBody.ReplaceNode(currentDeclaration, newDeclStatement);
        }

        var newMethod = containingMethod
            .WithParameterList(newParameterList)
            .WithBody(newBody);

        var declaringReplacements = new Dictionary<SyntaxNode, SyntaxNode>
        {
            [containingMethod] = newMethod
        };

        if (initializer != null)
        {
            foreach (var invocation in declaringInvocations
                         .Where(i => !containingMethod.Span.Contains(i.Span))
                         .Distinct())
            {
                declaringReplacements[invocation] = WithAddedArgument(invocation, initializer, variableName);
            }
        }

        var newRoot = root.ReplaceNodes(
            declaringReplacements.Keys,
            (original, _) => declaringReplacements[original]);
        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // 3. Update call sites in other documents (spans still valid there).
        if (initializer != null && otherCallSites.Count > 0)
        {
            foreach (var group in otherCallSites.GroupBy(c => c.DocumentId))
            {
                var refDoc = newSolution.GetDocument(group.Key);
                if (refDoc == null)
                    continue;

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null)
                    continue;

                var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();
                foreach (var site in group.OrderByDescending(c => c.Span.Start))
                {
                    var invocation = RematchInvocation(refRoot, site.Span);
                    if (invocation == null || replacements.ContainsKey(invocation))
                        continue;

                    replacements[invocation] = WithAddedArgument(invocation, initializer, variableName);
                }

                if (replacements.Count == 0)
                    continue;

                var newRefRoot = refRoot.ReplaceNodes(
                    replacements.Keys,
                    (original, _) => replacements[original]);
                newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
            }
        }

        return newSolution;
    }

    private static InvocationExpressionSyntax WithAddedArgument(
        InvocationExpressionSyntax invocation,
        ExpressionSyntax initializer,
        string variableName)
    {
        var newArgument = SyntaxFactory.Argument(initializer);
        if (invocation.ArgumentList.Arguments.Any(a => a.NameColon != null))
        {
            newArgument = newArgument.WithNameColon(
                SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(variableName)));
        }

        return invocation.WithArgumentList(invocation.ArgumentList.AddArguments(newArgument));
    }

    /// <summary>
    /// True when the local is <c>ref</c> / <c>ref readonly</c> (symbol
    /// <see cref="RefKind"/> or <see cref="RefExpressionSyntax"/> initializer).
    /// </summary>
    private static bool IsRefLocal(
        VariableDeclaratorSyntax localDecl,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (localDecl.Initializer?.Value is RefExpressionSyntax)
            return true;

        var localSymbol = semanticModel.GetDeclaredSymbol(localDecl, cancellationToken) as ILocalSymbol;
        return localSymbol != null && localSymbol.RefKind != RefKind.None;
    }

    private static async Task<IReadOnlyList<CallSite>> CollectCallSitesAsync(
        Document declaringDocument,
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var callSites = new List<CallSite>();
        var seen = new HashSet<(DocumentId Id, int SpanStart, int SpanEnd)>();
        var declaringPathKey = declaringDocument.FilePath != null
            ? PathResolver.GetPathComparisonKey(declaringDocument.FilePath)
            : null;

        await CollectCallSitesForSymbolAsync(
            methodSymbol,
            declaringDocument,
            declaringDocument,
            declaringPathKey,
            solution,
            callSites,
            seen,
            cancellationToken);

        // Linked sibling DocumentIds bind a distinct IMethodSymbol for the same
        // physical declaration. SymbolFinder on the primary symbol misses callers
        // that bind only under those sibling compilations.
        if (declaringPathKey != null)
        {
            foreach (var project in solution.Projects)
            {
                foreach (var siblingDoc in project.Documents)
                {
                    if (siblingDoc.Id == declaringDocument.Id || siblingDoc.FilePath == null)
                        continue;
                    if (PathResolver.GetPathComparisonKey(siblingDoc.FilePath) != declaringPathKey)
                        continue;

                    var siblingRoot = await siblingDoc.GetSyntaxRootAsync(cancellationToken);
                    var siblingModel = await siblingDoc.GetSemanticModelAsync(cancellationToken);
                    if (siblingRoot == null || siblingModel == null)
                        continue;

                    var rematched = RematchMethod(siblingRoot, methodSyntax);
                    if (rematched == null)
                        continue;

                    var siblingSymbol = siblingModel.GetDeclaredSymbol(rematched, cancellationToken) as IMethodSymbol;
                    if (siblingSymbol == null)
                        continue;

                    await CollectCallSitesForSymbolAsync(
                        siblingSymbol,
                        siblingDoc,
                        declaringDocument,
                        declaringPathKey,
                        solution,
                        callSites,
                        seen,
                        cancellationToken);
                }
            }
        }

        return callSites;
    }

    private static async Task CollectCallSitesForSymbolAsync(
        IMethodSymbol methodSymbol,
        Document symbolDeclaringDocument,
        Document primaryDeclaringDocument,
        string? declaringPathKey,
        Solution solution,
        List<CallSite> callSites,
        HashSet<(DocumentId Id, int SpanStart, int SpanEnd)> seen,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol, solution, cancellationToken);

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Location.Kind != LocationKind.SourceFile)
                    continue;

                var document = solution.GetDocument(location.Document.Id) ?? location.Document;

                // Linked sibling DocumentIds share the physical path but belong
                // to another compilation. In-file call sites are rewritten only
                // via the primary declaring DocumentId; sibling compilations are
                // consulted for callers in other files.
                if (document.FilePath != null &&
                    declaringPathKey != null &&
                    PathResolver.GetPathComparisonKey(document.FilePath) == declaringPathKey)
                {
                    if (symbolDeclaringDocument.Id != primaryDeclaringDocument.Id ||
                        document.Id != primaryDeclaringDocument.Id)
                    {
                        continue;
                    }
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation == null)
                    continue;

                var key = (document.Id, invocation.Span.Start, invocation.Span.End);
                if (!seen.Add(key))
                    continue;

                callSites.Add(new CallSite(document.Id, invocation.Span));
            }
        }
    }

    private static MethodDeclarationSyntax? RematchMethod(SyntaxNode root, MethodDeclarationSyntax original)
    {
        var candidates = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == original.Identifier.Text)
            .ToList();
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        return candidates
            .OrderBy(m => Math.Abs(m.SpanStart - original.SpanStart))
            .ThenBy(m => m.Span.Length)
            .FirstOrDefault();
    }

    private static InvocationExpressionSyntax? RematchInvocation(SyntaxNode root, TextSpan span)
    {
        if (span.Start < 0 || span.End > root.FullSpan.End)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .OrderBy(i => Math.Abs(i.SpanStart - span.Start))
                .ThenBy(i => i.Span.Length)
                .FirstOrDefault();
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        return node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault()
            ?? root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Span.OverlapsWith(span) || span.OverlapsWith(i.Span))
                .OrderBy(i => Math.Abs(i.SpanStart - span.Start))
                .ThenBy(i => i.Span.Length)
                .FirstOrDefault();
    }

    private readonly record struct CallSite(DocumentId DocumentId, TextSpan Span);

    /// <summary>
    /// Resolves the local variable declarator. Omitted <paramref name="column"/>
    /// keeps today's start-line equality on the local declaration statement,
    /// then <paramref name="variableName"/> <c>FirstOrDefault</c>. When set,
    /// picks the matching <c>VariableDeclaratorSyntax</c> whose identifier or
    /// declaration span covers that 1-based column (identifier preferred,
    /// then smallest covering declarator). A continuation-line identifier is
    /// eligible — do not require the declaration statement to start on
    /// <paramref name="line"/>. If nothing covers, or the covering declarator
    /// is not <paramref name="variableName"/>, return null
    /// (<c>SymbolNotFound</c>) rather than falling back to FirstOrDefault.
    /// </summary>
    internal static VariableDeclaratorSyntax? FindLocalDeclarator(
        SyntaxNode root,
        string variableName,
        int line,
        int? column)
    {
        if (!column.HasValue)
        {
            // Today's pick exactly: start-line equality on the local
            // declaration statement, then variableName FirstOrDefault.
            // Do not force column 1. Do not rewrite to covering-span.
            var targetLine = line - 1;
            return root.DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .Where(l => l.GetLocation().GetLineSpan().StartLinePosition.Line == targetLine)
                .SelectMany(l => l.Declaration.Variables)
                .FirstOrDefault(v => v.Identifier.Text == variableName);
        }

        // Column set: do not require the declaration statement to start
        // on `line`. Scan every local declarator, prefer the identifier
        // hit, then the smallest covering declarator. If the covering
        // declarator is not variableName, return null rather than
        // falling back to FirstOrDefault.
        var covering = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Parent?.Parent is LocalDeclarationStatementSyntax)
            .Where(declarator => LocalCoverage.LocalCoversColumn(declarator, line, column.Value))
            .OrderBy(declarator => LocalCoverage.IdentifierCoversColumn(declarator, line, column.Value) ? 0 : 1)
            .ThenBy(declarator => LocalCoverage.SmallestCoveringSpanLength(declarator, line, column.Value))
            .FirstOrDefault();

        if (covering == null || covering.Identifier.Text != variableName)
            return null;

        return covering;
    }

}

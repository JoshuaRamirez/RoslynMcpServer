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
    /// same text is applied to every sibling <see cref="DocumentId"/>
    /// (<see cref="PathResolver.GetPathComparisonKey"/>). Ineligible locals
    /// (not inside a method body, inside a local function, using declaration,
    /// no initializer, duplicate parameter name, uneditable / source-generated
    /// docs) are skipped rather than failing the walk. Deterministic
    /// <c>SpanStart</c> order within a file. When every file is a no-op,
    /// succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        IntroduceParameterParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        // One physical path may appear as multiple Documents when linked into
        // several projects. Rewrite once per normalized path and apply the same
        // text to every sibling DocumentId (ConvertToBlockBody allFiles / Codex).
        var documentGroups = allDocuments
            .GroupBy(d => PathResolver.GetPathComparisonKey(d.FilePath!), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                .ToList())
            .OrderBy(group => group[0].FilePath, StringComparer.Ordinal)
            .ToList();

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

                var updatedPrimary = updated.GetDocument(primary.Id)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
                var afterText = await updatedPrimary.GetTextAsync(cancellationToken);

                currentSolution = updated;
                foreach (var linked in linkedDocuments)
                {
                    if (linked.Id == primary.Id)
                        continue;

                    var sibling = currentSolution.GetDocument(linked.Id);
                    if (sibling == null || sibling is SourceGeneratedDocument)
                        continue;
                    if (!DocumentEditableHelpers.IsDocumentEditable(sibling, Context.Workspace))
                        continue;

                    currentSolution = currentSolution.WithDocumentText(sibling.Id, afterText);
                }

                promotedCountByDoc[primary.Id] =
                    promotedCountByDoc.GetValueOrDefault(primary.Id) + 1;
            }
        }

        var documentsToCompare = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

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
        // (promoting onto the outer method would be incorrect).
        var nearestExecutable = localDecl.Ancestors()
            .FirstOrDefault(n => n is MethodDeclarationSyntax or LocalFunctionStatementSyntax);
        if (nearestExecutable is not MethodDeclarationSyntax containingMethod)
            return null;

        if (containingMethod.Body == null)
            return null;

        var variableName = localDecl.Identifier.Text;
        if (containingMethod.ParameterList.Parameters.Any(p => p.Identifier.Text == variableName))
            return null;

        var methodSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken);
        if (methodSymbol == null)
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
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
        // 1. Add parameter to method signature
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(variableName))
            .WithType(paramType.WithTrailingTrivia(SyntaxFactory.Space));

        var newParameterList = containingMethod.ParameterList.AddParameters(newParam);

        // 2. Remove the selected local declarator (do not rematch by name /
        // stale SpanStart / line after rewrite — keep today's selected node).
        var declarationStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var newBody = containingMethod.Body!;

        if (declarationStatement.Declaration.Variables.Count == 1)
        {
            // Remove entire statement
            newBody = newBody.RemoveNode(declarationStatement, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            // Remove just this variable from the multi-variable declaration
            var newDeclaration = declarationStatement.Declaration.RemoveNode(localDecl, SyntaxRemoveOptions.KeepNoTrivia)!;
            var newDeclStatement = declarationStatement.WithDeclaration(newDeclaration);
            newBody = (BlockSyntax)newBody.ReplaceNode(declarationStatement, newDeclStatement);
        }

        var newMethod = containingMethod
            .WithParameterList(newParameterList)
            .WithBody(newBody);

        var newRoot = root.ReplaceNode(containingMethod, newMethod);
        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // 3. Update call sites: add the initializer value as argument
        if (initializer != null)
        {
            var references = await SymbolFinder.FindReferencesAsync(
                methodSymbol, newSolution, cancellationToken);

            foreach (var reference in references.SelectMany(r => r.Locations))
            {
                var refDoc = newSolution.GetDocument(reference.Document.Id);
                if (refDoc == null) continue;

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null) continue;

                var refNode = refRoot.FindNode(reference.Location.SourceSpan);
                var invocation = refNode.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation != null)
                {
                    var newArgument = SyntaxFactory.Argument(initializer);
                    var newArgList = invocation.ArgumentList.AddArguments(newArgument);
                    var newInvocation = invocation.WithArgumentList(newArgList);
                    var newRefRoot = refRoot.ReplaceNode(invocation, newInvocation);
                    newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
                }
            }
        }

        return newSolution;
    }

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

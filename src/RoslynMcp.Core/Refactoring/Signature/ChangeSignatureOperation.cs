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

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Changes a method's signature by adding, removing, or reordering parameters.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and applies the same <c>parameters</c> list to every
/// eligible method, skipping ineligible methods rather than throwing.
/// </summary>
public sealed class ChangeSignatureOperation : RefactoringOperationBase<ChangeSignatureParams>
{
    /// <summary>
    /// Creates a new change signature operation.
    /// </summary>
    public ChangeSignatureOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ChangeSignatureParams @params) => Validate(@params);

    /// <summary>
    /// Validates change-signature parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ChangeSignatureParams @params)
    {
        if (@params.Parameters == null || @params.Parameters.Count == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameters is required.");

        foreach (var change in @params.Parameters)
        {
            if (string.IsNullOrWhiteSpace(change.Name))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Each parameter change requires a name.");

            if (change.OriginalName == null && !change.Remove && string.IsNullOrWhiteSpace(change.Type))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "New parameters require a type.");
        }

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

        var sourceFile = @params.SourceFile!;

        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ChangeSignatureParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var methodName = @params.MethodName!;

        var document = GetDocumentOrThrow(sourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find method declaration
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

        // Build new parameter list
        var newParameters = BuildNewParameterList(methodSymbol.Parameters.ToList(), @params.Parameters);

        // Find all call sites
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol,
            Context.Solution,
            cancellationToken);

        var callSites = references
            .SelectMany(r => r.Locations)
            .Where(loc => loc.Document.Id != document.Id || !loc.Location.SourceSpan.IntersectsWith(methodDecl.Span))
            .ToList();

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, sourceFile, methodName, methodSymbol.Parameters.ToList(), newParameters, callSites.Count);
        }

        var newSolution = await ApplySignatureChangeAsync(
            document,
            root,
            methodDecl,
            methodSymbol,
            newParameters,
            @params.Parameters,
            cancellationToken);

        // Commit changes
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
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            callSites.Count,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>IntroduceParameterOperation.ExecuteAllFilesAsync</c>)
    /// and applies <paramref name="params"/>.Parameters to every eligible
    /// <see cref="MethodDeclarationSyntax"/>. Optional <c>sourceFile</c> limits
    /// via <see cref="DocumentSourceFileFilter"/>. Linked documents that share a
    /// physical path are rewritten once and the same text is applied to every
    /// sibling <see cref="DocumentId"/> via <see cref="Solution.GetChanges(Solution)"/>
    /// coalesce (prefer a changed DocumentId as source). Methods missing any
    /// <c>originalName</c>, kept <c>ref</c>/<c>out</c>/<c>in</c>/<c>params</c>
    /// parameters (modifiers not preserved by <c>CreateParameterSyntax</c>),
    /// uneditable / source-generated docs, and otherwise inapplicable methods
    /// are skipped rather than failing the walk.
    /// Deterministic <c>SpanStart</c> order within a file. When every file is
    /// a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ChangeSignatureParams @params,
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

        var documentGroups = allDocuments
            .GroupBy(d => PathResolver.GetPathComparisonKey(d.FilePath!), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                .ToList())
            .OrderBy(group => group[0].FilePath, StringComparer.Ordinal)
            .ToList();

        var changedCountByDoc = new Dictionary<DocumentId, int>();

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
                foreach (var methodDecl in CollectMethods(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        updated = await TryChangeOneAsync(
                            currentDocument,
                            root,
                            semanticModel,
                            methodDecl,
                            @params.Parameters,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        // Skip ineligible methods rather than failing the walk.
                        updated = null;
                    }

                    if (updated != null)
                        break;
                }

                if (updated == null)
                    break;

                var beforeSolution = currentSolution;
                currentSolution = updated;
                var changedDocIds = new HashSet<DocumentId>();
                var changedPathKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var projectChanges in updated.GetChanges(beforeSolution).GetProjectChanges())
                {
                    foreach (var docId in projectChanges.GetChangedDocuments())
                    {
                        changedDocIds.Add(docId);
                        var changedDoc = updated.GetDocument(docId);
                        if (changedDoc?.FilePath != null)
                            changedPathKeys.Add(PathResolver.GetPathComparisonKey(changedDoc.FilePath));
                    }
                }

                if (changedPathKeys.Count > 0)
                {
                    var allCurrent = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .Where(d => d.FilePath != null)
                        .ToList();

                    foreach (var pathKey in changedPathKeys)
                    {
                        var siblings = allCurrent
                            .Where(d => PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                            .ThenBy(d => d.Project.Name, StringComparer.Ordinal)
                            .ThenBy(d => d.Id.Id.ToString(), StringComparer.Ordinal)
                            .ToList();
                        if (siblings.Count <= 1)
                            continue;

                        var sourceDoc = siblings.FirstOrDefault(d =>
                                changedDocIds.Contains(d.Id) &&
                                d is not SourceGeneratedDocument &&
                                DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace))
                            ?? siblings.FirstOrDefault(d =>
                                d is not SourceGeneratedDocument &&
                                DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace));
                        if (sourceDoc == null)
                            continue;

                        var live = currentSolution.GetDocument(sourceDoc.Id);
                        if (live == null)
                            continue;
                        var sharedText = await live.GetTextAsync(cancellationToken);

                        foreach (var sibling in siblings)
                        {
                            if (sibling.Id == sourceDoc.Id)
                                continue;
                            var siblingLive = currentSolution.GetDocument(sibling.Id);
                            if (siblingLive == null || siblingLive is SourceGeneratedDocument)
                                continue;
                            if (!DocumentEditableHelpers.IsDocumentEditable(siblingLive, Context.Workspace))
                                continue;
                            currentSolution = currentSolution.WithDocumentText(sibling.Id, sharedText);
                        }
                    }
                }

                changedCountByDoc[primary.Id] =
                    changedCountByDoc.GetValueOrDefault(primary.Id) + 1;
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
                var changedCount = changedCountByDoc.GetValueOrDefault(document.Id);
                if (changedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        changedCount = Math.Max(changedCount, changedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = changedCount > 0
                        ? BuildAllFilesDescription(changedCount)
                        : "Update call sites of changed signatures",
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
    /// Preview description for a file that changed
    /// <paramref name="changedCount"/> method signatures.
    /// </summary>
    internal static string BuildAllFilesDescription(int changedCount) =>
        changedCount == 1
            ? "Change signature"
            : $"Change {changedCount} signatures";

    /// <summary>
    /// Collects every <see cref="MethodDeclarationSyntax"/> in
    /// <paramref name="root"/> in deterministic <c>SpanStart</c> then
    /// span-length order.
    /// </summary>
    internal static IReadOnlyList<MethodDeclarationSyntax> CollectMethods(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .OrderBy(m => m.SpanStart)
            .ThenBy(m => m.Span.Length)
            .ToList();

    /// <summary>
    /// True when every <c>originalName</c> in <paramref name="changes"/>
    /// exists on <paramref name="method"/>, and no kept parameter uses
    /// <c>ref</c>/<c>out</c>/<c>in</c>/<c>params</c> (bulk rewrite cannot
    /// preserve those modifiers yet — <c>CreateParameterSyntax</c> only
    /// emits type/name/default).
    /// </summary>
    internal static bool IsEligible(IMethodSymbol method, IReadOnlyList<ParameterChange> changes)
    {
        var byName = method.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.OriginalName == null)
                continue;
            if (!byName.TryGetValue(change.OriginalName, out var existing))
                return false;
            // Skipping Remove entries: dropped params need no modifier emit.
            if (change.Remove)
                continue;
            if (existing.RefKind != RefKind.None || existing.IsParams)
                return false;
        }

        return true;
    }

    private async Task<Solution?> TryChangeOneAsync(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDecl,
        IReadOnlyList<ParameterChange> changes,
        CancellationToken cancellationToken)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
            return null;

        if (!IsEligible(methodSymbol, changes))
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        var newParameters = BuildNewParameterList(methodSymbol.Parameters.ToList(), changes);
        // Already at the target signature — skip so the while-loop cannot
        // re-apply the same parameters list forever (unlike introduce_parameter,
        // the method stays present and still matches originalName eligibility).
        // Include defaults so default-only ParameterChange entries still apply (Codex).
        if (SignatureAlreadyMatches(methodDecl, methodSymbol, newParameters))
            return null;

        var beforeText = await document.GetTextAsync(cancellationToken);
        var newSolution = await ApplySignatureChangeAsync(
            document,
            root,
            methodDecl,
            methodSymbol,
            newParameters,
            changes,
            cancellationToken);

        var afterDocument = newSolution.GetDocument(document.Id);
        if (afterDocument == null)
            return null;

        var afterText = await afterDocument.GetTextAsync(cancellationToken);
        if (beforeText.ContentEquals(afterText))
        {
            // Declaring file unchanged but call sites may have changed — keep
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

    /// <summary>
    /// True when <paramref name="methodDecl"/> already has the same parameter
    /// names, types, and default-value expressions (in order) as
    /// <paramref name="newParameters"/>. Defaults are compared from syntax so
    /// default-only <see cref="ParameterChange"/> entries still apply.
    /// </summary>
    private static bool SignatureAlreadyMatches(
        MethodDeclarationSyntax methodDecl,
        IMethodSymbol method,
        IReadOnlyList<NewParameter> newParameters)
    {
        if (method.Parameters.Length != newParameters.Count)
            return false;
        if (methodDecl.ParameterList.Parameters.Count != newParameters.Count)
            return false;

        for (var i = 0; i < newParameters.Count; i++)
        {
            var existing = method.Parameters[i];
            var expected = newParameters[i];
            if (!string.Equals(existing.Name, expected.Name, StringComparison.Ordinal))
                return false;
            if (!string.Equals(existing.Type.ToDisplayString(), expected.Type, StringComparison.Ordinal))
                return false;

            var existingDefault = methodDecl.ParameterList.Parameters[i].Default?.Value.ToString();
            var expectedDefault = expected.DefaultValue;
            if (string.IsNullOrEmpty(existingDefault))
                existingDefault = null;
            if (string.IsNullOrEmpty(expectedDefault))
                expectedDefault = null;
            if (!string.Equals(existingDefault, expectedDefault, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private async Task<Solution> ApplySignatureChangeAsync(
        Document document,
        SyntaxNode root,
        MethodDeclarationSyntax methodDecl,
        IMethodSymbol methodSymbol,
        List<NewParameter> newParameters,
        IReadOnlyList<ParameterChange> changes,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol,
            document.Project.Solution,
            cancellationToken);

        var callSites = references
            .SelectMany(r => r.Locations)
            .Where(loc => loc.Document.Id != document.Id || !loc.Location.SourceSpan.IntersectsWith(methodDecl.Span))
            .ToList();

        var newParamSyntax = newParameters.Select(CreateParameterSyntax);
        var newParamList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamSyntax));
        var newMethodDecl = methodDecl.WithParameterList(newParamList);

        var newRoot = root.ReplaceNode(methodDecl, newMethodDecl);
        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        foreach (var callSite in callSites)
        {
            var callDoc = newSolution.GetDocument(callSite.Document.Id);
            if (callDoc == null) continue;

            var callRoot = await callDoc.GetSyntaxRootAsync(cancellationToken);
            if (callRoot == null) continue;

            var callNode = callRoot.FindNode(callSite.Location.SourceSpan);
            var invocation = callNode.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

            if (invocation != null)
            {
                var newInvocation = UpdateInvocation(
                    invocation,
                    methodSymbol.Parameters.ToList(),
                    newParameters,
                    changes);

                var newCallRoot = callRoot.ReplaceNode(invocation, newInvocation);
                newSolution = callDoc.WithSyntaxRoot(newCallRoot).Project.Solution;
            }
        }

        return newSolution;
    }

    /// <summary>
    /// Finds a method. Omitted <paramref name="column"/> keeps today's
    /// first-match (MethodName; Line when more than one match, start-line
    /// filter). When set, picks the smallest method whose identifier or
    /// declaration span covers that 1-based column. Do not require the
    /// declaration to start on <paramref name="line"/> when column is set
    /// — a split signature may put the identifier on a continuation line.
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
                .Where(m => MethodCoverage.MethodCoversColumn(m, line ?? StartLine(m), column.Value))
                .OrderBy(m => MethodCoverage.IdentifierCoversColumn(m, line ?? StartLine(m), column.Value) ? 0 : 1)
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

    private static List<NewParameter> BuildNewParameterList(
        List<IParameterSymbol> originalParams,
        IReadOnlyList<ParameterChange> changes)
    {
        var result = new List<NewParameter>();
        var originalMap = originalParams.ToDictionary(p => p.Name);

        // First pass: handle existing parameters and removals
        foreach (var change in changes.Where(c => c.OriginalName != null))
        {
            if (change.Remove) continue;

            if (!originalMap.TryGetValue(change.OriginalName!, out var originalParam))
            {
                throw new RefactoringException(
                    ErrorCodes.ParameterNotFound,
                    $"Parameter '{change.OriginalName}' not found in method.");
            }

            result.Add(new NewParameter
            {
                Name = change.Name,
                Type = change.Type ?? originalParam.Type.ToDisplayString(),
                DefaultValue = change.DefaultValue,
                Position = change.NewPosition,
                OriginalName = change.OriginalName
            });
        }

        // Second pass: add new parameters
        foreach (var change in changes.Where(c => c.OriginalName == null && !c.Remove))
        {
            result.Add(new NewParameter
            {
                Name = change.Name,
                Type = change.Type!,
                DefaultValue = change.DefaultValue,
                Position = change.NewPosition,
                OriginalName = null
            });
        }

        // Sort by position
        if (result.Any(p => p.Position.HasValue))
        {
            result = result
                .OrderBy(p => p.Position ?? int.MaxValue)
                .ThenBy(p => result.IndexOf(p))
                .ToList();
        }

        return result;
    }

    private static ParameterSyntax CreateParameterSyntax(NewParameter param)
    {
        var paramSyntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(param.Name))
            .WithType(SyntaxFactory.ParseTypeName(param.Type).WithTrailingTrivia(SyntaxFactory.Space));

        if (!string.IsNullOrEmpty(param.DefaultValue))
        {
            paramSyntax = paramSyntax.WithDefault(
                SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.ParseExpression(param.DefaultValue)));
        }

        return paramSyntax;
    }

    private static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax invocation,
        List<IParameterSymbol> originalParams,
        List<NewParameter> newParams,
        IReadOnlyList<ParameterChange> changes)
    {
        var originalArgs = invocation.ArgumentList.Arguments.ToList();
        var newArgs = new List<ArgumentSyntax>();

        // Build a map of original param name to argument
        var argMap = new Dictionary<string, ArgumentSyntax>();
        for (int i = 0; i < originalArgs.Count && i < originalParams.Count; i++)
        {
            var arg = originalArgs[i];
            var paramName = arg.NameColon?.Name.Identifier.Text ?? originalParams[i].Name;
            argMap[paramName] = arg;
        }

        foreach (var newParam in newParams)
        {
            if (newParam.OriginalName != null && argMap.TryGetValue(newParam.OriginalName, out var existingArg))
            {
                // Rename the argument if needed
                if (newParam.Name != newParam.OriginalName && existingArg.NameColon != null)
                {
                    existingArg = existingArg.WithNameColon(
                        SyntaxFactory.NameColon(newParam.Name));
                }
                newArgs.Add(existingArg);
            }
            else if (!string.IsNullOrEmpty(newParam.DefaultValue))
            {
                // New parameter with default - use default
                newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression(newParam.DefaultValue)));
            }
            else
            {
                // New parameter without default - add placeholder
                newArgs.Add(SyntaxFactory.Argument(
                    SyntaxFactory.ParseExpression($"default /* TODO: {newParam.Name} */")));
            }
        }

        return invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string sourceFile,
        string methodName,
        List<IParameterSymbol> originalParams,
        List<NewParameter> newParams,
        int callSiteCount)
    {
        var oldSig = string.Join(", ", originalParams.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
        var newSig = string.Join(", ", newParams.Select(p =>
            string.IsNullOrEmpty(p.DefaultValue)
                ? $"{p.Type} {p.Name}"
                : $"{p.Type} {p.Name} = {p.DefaultValue}"));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = sourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Change signature of '{methodName}' ({callSiteCount} call sites to update)",
                BeforeSnippet = $"{methodName}({oldSig})",
                AfterSnippet = $"{methodName}({newSig})"
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed class NewParameter
    {
        public required string Name { get; init; }
        public required string Type { get; init; }
        public string? DefaultValue { get; init; }
        public int? Position { get; init; }
        public string? OriginalName { get; init; }
    }
}

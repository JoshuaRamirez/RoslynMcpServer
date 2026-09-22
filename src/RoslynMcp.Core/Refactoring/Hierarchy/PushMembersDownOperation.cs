using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Moves selected members from a base type down onto derived types.
/// Honors optional <c>line</c> and <c>column</c> to disambiguate
/// same-named types in one file (identifier preferred, then smallest
/// containing type). Omitted column keeps today's typeName + optional
/// line pick. Omitted line keeps today's <c>TypeDeclarationSyntax</c>
/// <c>FirstOrDefault</c> pick (enum and
/// <c>DelegateDeclarationSyntax</c> do not participate).
/// When line is set, a covering enum or delegate is included so it
/// reaches <c>InvalidSymbolKind</c> rather than retargeting a later class.
/// After the push (source rewrite + members added to derived types), the
/// selected declaration is recovered by a per-execution syntax annotation
/// (stripped before commit).
/// </summary>
public sealed class PushMembersDownOperation : RefactoringOperationBase<PushMembersDownParams>
{
    /// <summary>
    /// Creates a new push-members-down operation.
    /// </summary>
    public PushMembersDownOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(PushMembersDownParams @params) => Validate(@params);

    /// <summary>
    /// Validates push-members-down parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(PushMembersDownParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.TypeName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue ||
                @params.Members != null ||
                @params.TargetDerivedTypes != null)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with typeName, line, column, members, or targetDerivedTypes.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (@params.Members == null || @params.Members.Count == 0 || @params.Members.All(string.IsNullOrWhiteSpace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "members is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static void ValidateSourceFilePath(string sourceFile)
    {
        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        PushMembersDownParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Optional line/column disambiguates same-named types. Omitted
        // column keeps today's TypeDeclarationSyntax FirstOrDefault
        // pick (enum and DelegateDeclarationSyntax do not participate).
        // Line set also includes a covering enum or delegate so it
        // reaches InvalidSymbolKind instead of retargeting a later class.
        var found = FindTypeDeclaration(root, @params.TypeName!, @params.Line, @params.Column);
        if (found == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName!}' not found in file.");
        }

        var sourceSymbol = semanticModel.GetDeclaredSymbol(found, cancellationToken) as INamedTypeSymbol;
        if (sourceSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        if (found is not TypeDeclarationSyntax sourceDecl)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{sourceSymbol.Name}' is not a supported target for push_members_down.");
        }

        var members = FindMembersToPush(sourceDecl, @params.Members!, semanticModel, cancellationToken);
        var targets = await GetDerivedTypes(sourceSymbol, @params.TargetDerivedTypes, cancellationToken);

        if (targets.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.DerivedClassesNotFound,
                $"Type '{sourceSymbol.Name}' has no derived types to push members to.");
        }

        foreach (var target in targets)
            ValidateDerivedIsEditable(target);

        var leaveAbstract = @params.LeaveAbstract && sourceSymbol.TypeKind != TypeKind.Interface;
        ValidateMembersForPush(members, sourceSymbol, targets, leaveAbstract);
        if (leaveAbstract)
        {
            var allDerived = await GetDerivedTypes(sourceSymbol, targetNames: null, cancellationToken);
            ValidateLeaveAbstractCoversConcreteDerived(sourceSymbol, allDerived, targets);
        }

        await ValidateNoBreakingReferencesAsync(
            members, sourceSymbol, targets, leaveAbstract, Context.Solution, cancellationToken);

        var pushedNames = members.Select(m => m.Name).ToList();
        var derivedUpdates = new List<DerivedUpdate>();

        foreach (var target in targets)
        {
            var original = await GetTypeDeclarationAsync(target, cancellationToken);
            var copies = members
                .Select(member => ConvertForDerived(member, sourceSymbol, target, semanticModel, leaveAbstract))
                .ToList();
            derivedUpdates.Add(new DerivedUpdate(target, original, AddMembersToType(original, copies)));
        }

        var sourceReplacement = BuildSourceReplacement(sourceDecl, members, sourceSymbol, leaveAbstract);

        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                sourceSymbol,
                pushedNames,
                sourceDecl,
                sourceReplacement,
                derivedUpdates);
        }

        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later push on another type would
        // recover the stale node via FirstOrDefault.
        // Annotate before the rewrite. Same-file push rewrites the
        // source type and adds members to derived types — both shift
        // later same-named types. Do not re-find with stale SpanStart
        // or line.
        var sourceTypeAnnotation = new SyntaxAnnotation("push-members-down-source-type");
        var previousTree = root.SyntaxTree;
        root = root.ReplaceNode(
            sourceDecl,
            sourceDecl.WithAdditionalAnnotations(sourceTypeAnnotation));
        document = document.WithSyntaxRoot(root);
        var annotatedSolution = document.Project.Solution;
        // If GetDocument(oldTree) misses after the annotation rewrite,
        // look up by file path and rematch by span (same as
        // pull_members_up / extract_base_class / implement_abstract).
        document = annotatedSolution.GetDocument(previousTree)
            ?? DocumentForTreeHelpers.GetDocumentForTree(annotatedSolution, previousTree, @params.TypeName!);
        root = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        sourceDecl = RecoverAnnotatedType(
            root,
            sourceTypeAnnotation,
            sourceDecl,
            @params.TypeName!);

        // Strip the per-execution annotation so it does not linger in the
        // workspace after commit.
        sourceReplacement = (TypeDeclarationSyntax)sourceReplacement.WithoutAnnotations(sourceTypeAnnotation);

        var rematchedUpdates = RematchDerivedUpdates(root, document, derivedUpdates);

        var solution = await ApplyChangesAsync(
            document,
            sourceDecl,
            sourceReplacement,
            rematchedUpdates,
            cancellationToken);

        var commitResult = await CommitChangesAsync(solution, cancellationToken);

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
                Name = sourceSymbol.Name,
                FullyQualifiedName = sourceSymbol.ToDisplayString(),
                Kind = sourceSymbol.TypeKind == TypeKind.Interface
                    ? Contracts.Enums.SymbolKind.Interface
                    : Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// helpers as <c>PullMembersUpOperation.ExecuteAllFilesAsync</c> /
    /// <c>ExtractBaseClassOperation.ExecuteAllFilesAsync</c>) and pushes every
    /// pushable member from each eligible base type onto all direct derived
    /// types (same as omitted <c>targetDerivedTypes</c>). Optional
    /// <c>sourceFile</c> limits via <see cref="DocumentSourceFileFilter"/>.
    /// Linked documents that share a physical path are rewritten once and
    /// sibling text is coalesced via
    /// <see cref="AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync"/>.
    /// Types with no derived targets, empty pushable member sets, unsupported
    /// kinds, uneditable / source-generated docs, non-editable derived types,
    /// and linked multi-views are skipped rather than failing the walk.
    /// Members inserted onto a derived type by an earlier push in this walk
    /// are tracked by cascade key (signature for methods/indexers, after
    /// constructed-base type substitution) and skipped when that type is later
    /// visited as a source, so they are not cascaded further down the hierarchy.
    /// De-duplication is per declaration (<c>TypeWalkKey</c> + document + member
    /// cascade keys), so partial types with pushable members in multiple files —
    /// or multiple parts in one file — are all visited. Skipped declarations are
    /// revisited in later passes once dependent parts unlock them. Each
    /// site collects a type-wide member batch across partials so cyclic
    /// cross-partial dependencies can move together. Linked
    /// multi-view path counts come from the full solution (independent of
    /// <c>sourceFile</c>); multi-view targets are skipped. Deterministic
    /// <c>SpanStart</c> order within a file. When every file is a no-op,
    /// succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        PushMembersDownParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        // Linked-path counts must cover the whole solution — not just the
        // sourceFile-filtered walk — so DeclaringPathHasLinkedMultiView still
        // sees multi-view derived targets outside the filtered source set.
        var linkedPathCounts = BuildLinkedPathCounts(originalSolution);
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);
        var pushedCountByDoc = new Dictionary<DocumentId, int>();
        // Type-wide keys (type + cascade keys of the full partial batch).
        // Claiming the whole type batch prevents a later partial visit from
        // re-pushing the same members after a successful type-wide apply.
        var processedDeclarations = new HashSet<string>(StringComparer.Ordinal);
        // Cascade keys are signature-based (MemberCascadeKey), not bare names,
        // so inserting Root.M(string) does not suppress Middle.M(int).
        var insertedMembersByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Revisit skipped declarations after later partial parts unlock them
        // (e.g. field blocked by a method in another file that later moves).
        bool madeProgress;
        do
        {
            madeProgress = false;
            foreach (var linkedDocuments in documentGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Linked multi-project views of the same path can diverge under
                // preprocessor symbols; skip rather than coalescing a push that
                // only one compilation can honor. Same contract as
                // pull_members_up / extract_base_class allFiles.
                if (linkedDocuments.Count > 1)
                    continue;

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
                    foreach (var typeNode in TypeDeclarationHelpers.CollectTypeDeclarations(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (typeNode is not TypeDeclarationSyntax typeDeclaration)
                            continue;

                        var sourceSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
                        if (sourceSymbol == null)
                            continue;

                        var typeKey = TypeWalkKeyHelpers.TypeWalkKey(currentDocument.Project.Id, sourceSymbol);

                        IReadOnlyList<INamedTypeSymbol> targets;
                        try
                        {
                            targets = await GetDerivedTypes(
                                sourceSymbol, targetNames: null, currentSolution, cancellationToken);
                        }
                        catch (RefactoringException)
                        {
                            continue;
                        }

                        if (targets.Count == 0)
                            continue;

                        // Skip when any derived target declares on a linked multi-view
                        // path — coalescing would overwrite divergent siblings.
                        if (targets.Any(t => DeclaringPathHasLinkedMultiView(t, currentSolution, linkedPathCounts)))
                            continue;

                        try
                        {
                            foreach (var target in targets)
                                ValidateDerivedIsEditable(target);
                        }
                        catch (RefactoringException)
                        {
                            continue;
                        }

                        var leaveAbstract = @params.LeaveAbstract && sourceSymbol.TypeKind != TypeKind.Interface;

                        try
                        {
                            // Type-wide batch: collect pushable members from every
                            // partial declaration so cyclic cross-partial deps
                            // (A↔B) validate and move together.
                            var members = await CollectTypeWidePushableMembersAsync(
                                sourceSymbol,
                                currentSolution,
                                leaveAbstract,
                                insertedMembersByType,
                                typeKey,
                                linkedPathCounts,
                                cancellationToken);

                            if (members.Count == 0)
                                continue;

                            var declarationKey = typeKey + "|typewide|" +
                                string.Join("\0", members
                                    .Select(m => MemberCascadeKey(m.Symbol))
                                    .OrderBy(k => k, StringComparer.Ordinal));
                            if (processedDeclarations.Contains(declarationKey))
                                continue;

                            ValidateMembersForPush(members, sourceSymbol, targets, leaveAbstract);
                            if (leaveAbstract)
                            {
                                ValidateLeaveAbstractCoversConcreteDerived(sourceSymbol, targets, targets);
                            }

                            await ValidateNoBreakingReferencesAsync(
                                members, sourceSymbol, targets, leaveAbstract, currentSolution, cancellationToken);

                            var derivedUpdates = new List<DerivedUpdate>();
                            foreach (var target in targets)
                            {
                                var original = await GetTypeDeclarationAsync(target, cancellationToken);
                                var copies = new List<MemberDeclarationSyntax>();
                                foreach (var member in members)
                                {
                                    var memberModel = await GetSemanticModelForSyntaxAsync(
                                        currentSolution, member.Syntax, cancellationToken)
                                        ?? semanticModel;
                                    copies.Add(ConvertForDerived(
                                        member, sourceSymbol, target, memberModel, leaveAbstract));
                                }

                                derivedUpdates.Add(new DerivedUpdate(
                                    target, original, AddMembersToType(original, copies)));
                            }

                            // Rewrite every partial that owns some of the batch.
                            var sourceUpdates = members
                                .Select(m => m.Syntax.Ancestors().OfType<TypeDeclarationSyntax>().First())
                                .Distinct()
                                .Select(decl => (
                                    decl,
                                    BuildSourceReplacement(decl, members, sourceSymbol, leaveAbstract)))
                                .ToList();

                            updated = await ApplyChangesAsync(
                                currentSolution,
                                sourceUpdates,
                                derivedUpdates,
                                cancellationToken);

                            if (updated == null)
                                continue;

                            // Record cascade keys inserted onto each derived target so a
                            // later visit of that type does not cascade those overloads.
                            foreach (var target in targets)
                            {
                                var targetProjectId = ResolveSymbolProjectId(currentSolution, target)
                                    ?? currentDocument.Project.Id;
                                var targetKey = TypeWalkKeyHelpers.TypeWalkKey(targetProjectId, target);
                                if (!insertedMembersByType.TryGetValue(targetKey, out var insertedOnTarget))
                                {
                                    insertedOnTarget = new HashSet<string>(StringComparer.Ordinal);
                                    insertedMembersByType[targetKey] = insertedOnTarget;
                                }

                                foreach (var member in members)
                                {
                                    // Record the signature as it will appear on the
                                    // derived type after type-parameter substitution
                                    // (Root<T>.M(T) → Middle:Root<int> records M(int)).
                                    insertedOnTarget.Add(
                                        MemberCascadeKeyForTarget(member.Symbol, sourceSymbol, target));
                                }
                            }

                            processedDeclarations.Add(declarationKey);
                            break;
                        }
                        catch (RefactoringException)
                        {
                            updated = null;
                            continue;
                        }
                    }

                    if (updated == null)
                        break;

                    var beforeSolution = currentSolution;
                    currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                        beforeSolution,
                        updated,
                        Context.Workspace,
                        cancellationToken);

                    pushedCountByDoc[primary.Id] =
                        pushedCountByDoc.GetValueOrDefault(primary.Id) + 1;
                    madeProgress = true;
                }
            }
        } while (madeProgress);

        var documentsToCompare = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution)
            .Concat(
                currentSolution.Projects
                    .SelectMany(p => p.Documents)
                    .Where(d => d.FilePath != null &&
                                d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                                originalSolution.GetDocument(d.Id) == null))
            .GroupBy(d => d.Id)
            .Select(g => g.First())
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
            if (currentDocument == null || originalDocument == null)
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
                var pushedCount = pushedCountByDoc.GetValueOrDefault(document.Id);
                if (pushedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        pushedCount = Math.Max(pushedCount, pushedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = pushedCount > 0
                        ? BuildAllFilesDescription(pushedCount)
                        : "Update push_members_down rewrites",
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
    /// Resolves the <see cref="ProjectId"/> that owns
    /// <paramref name="symbol"/>'s declaring syntax, when available.
    /// </summary>
    private static ProjectId? ResolveSymbolProjectId(Solution solution, ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var document = solution.GetDocument(reference.SyntaxTree);
            if (document != null)
                return document.Project.Id;
        }

        return null;
    }

    /// <summary>
    /// Preview description for a file that pushed members from
    /// <paramref name="pushedCount"/> base types.
    /// </summary>
    internal static string BuildAllFilesDescription(int pushedCount) =>
        pushedCount == 1
            ? "Push members down"
            : $"Push members down from {pushedCount} types";

    /// <summary>
    /// Collects every pushable member across all editable partial declarations
    /// of <paramref name="sourceSymbol"/>, cascade-filtered against members
    /// already inserted onto this type. Enables cyclic cross-partial
    /// dependencies to validate and move as one batch.
    /// </summary>
    private async Task<List<PushableMember>> CollectTypeWidePushableMembersAsync(
        INamedTypeSymbol sourceSymbol,
        Solution solution,
        bool leaveAbstract,
        IReadOnlyDictionary<string, HashSet<string>> insertedMembersByType,
        string typeKey,
        IReadOnlyDictionary<string, int> linkedPathCounts,
        CancellationToken cancellationToken)
    {
        var members = new List<PushableMember>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var syntaxRef in sourceSymbol.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = solution.GetDocument(syntaxRef.SyntaxTree);
            if (document == null ||
                document is SourceGeneratedDocument ||
                !DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            {
                continue;
            }

            // Skip linked multi-view declarations of the source itself.
            if (document.FilePath != null)
            {
                var pathKey = PathResolver.GetPathComparisonKey(document.FilePath);
                if (linkedPathCounts.TryGetValue(pathKey, out var viewCount) && viewCount > 1)
                    continue;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null)
                continue;

            if (await syntaxRef.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax decl)
                continue;

            var names = CollectPushableMemberNames(decl, model, leaveAbstract, cancellationToken);
            if (names.Count == 0)
                continue;

            foreach (var member in FindMembersToPush(decl, names, model, cancellationToken))
            {
                // Cascade keys alone collapse partial method definition +
                // implementation pairs to one entry; keep both so the rewrite
                // moves the matched pair together.
                if (seenKeys.Add(MemberBatchIdentityKey(member.Symbol, member.Syntax)))
                    members.Add(member);
            }
        }

        if (insertedMembersByType.TryGetValue(typeKey, out var insertedHere) &&
            insertedHere.Count > 0)
        {
            members = members
                .Where(m => !insertedHere.Contains(MemberCascadeKey(m.Symbol)))
                .ToList();
        }

        return members;
    }

    private static async Task<SemanticModel?> GetSemanticModelForSyntaxAsync(
        Solution solution,
        SyntaxNode syntax,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(syntax.SyntaxTree)
            ?? DocumentForTreeHelpers.GetDocumentByFilePath(solution, syntax.SyntaxTree);
        if (document == null)
            return null;

        return await document.GetSemanticModelAsync(cancellationToken);
    }

    private static List<string> CollectPushableMemberNames(
        TypeDeclarationSyntax typeDeclaration,
        SemanticModel semanticModel,
        bool leaveAbstract,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var (name, symbol, _) in EnumerateDeclaredMembers(
                     typeDeclaration, semanticModel, cancellationToken))
        {
            if (symbol == null || !IsSupportedMember(symbol))
                continue;

            // When leaveAbstract is set, skip members that cannot become abstract
            // so ValidateMembersForPush does not discard the whole site.
            if (leaveAbstract && !CanBeAbstract(symbol))
                continue;

            names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Stable cascade-prevention key for a member. Methods and indexers include
    /// their parameter signature so inserting one overload does not suppress
    /// another with the same metadata name. Must omit the containing type —
    /// after a push the member is redeclared on the derived type, and a
    /// containing-type-qualified display string would not match the key
    /// recorded at insertion time.
    /// </summary>
    private static readonly SymbolDisplayFormat CascadeKeyFormat = new(
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    internal static string MemberCascadeKey(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.ToDisplayString(CascadeKeyFormat),
            IPropertySymbol { IsIndexer: true } indexer =>
                indexer.ToDisplayString(CascadeKeyFormat),
            _ => symbol.Name
        };

    /// <summary>
    /// Collection-phase identity for type-wide batches. Same as
    /// <see cref="MemberCascadeKey"/> except partial method definition and
    /// implementation parts stay distinct (syntax span identity) so both
    /// declarations enter the batch and move together.
    /// </summary>
    internal static string MemberBatchIdentityKey(ISymbol symbol, MemberDeclarationSyntax syntax)
    {
        var key = MemberCascadeKey(symbol);
        if (symbol is IMethodSymbol method &&
            (method.IsPartialDefinition ||
             method.PartialDefinitionPart != null ||
             method.PartialImplementationPart != null))
        {
            var part = method.IsPartialDefinition ? "partial-def" : "partial-impl";
            return key + "|" + part + "|" + syntax.SpanStart;
        }

        return key;
    }

    /// <summary>
    /// Cascade key for a member as it will appear on <paramref name="target"/>
    /// after constructed-base type-parameter substitution. Falls back to
    /// <see cref="MemberCascadeKey"/> when there is no generic substitution.
    /// </summary>
    internal static string MemberCascadeKeyForTarget(
        ISymbol symbol,
        INamedTypeSymbol source,
        INamedTypeSymbol target)
    {
        var constructed = GetConstructedBase(source, target);
        if (constructed == null || constructed.TypeArguments.Length == 0)
            return MemberCascadeKey(symbol);

        foreach (var candidate in constructed.GetMembers(symbol.Name))
        {
            if (SymbolEqualityComparer.Default.Equals(
                    candidate.OriginalDefinition, symbol.OriginalDefinition))
            {
                return MemberCascadeKey(candidate);
            }
        }

        return MemberCascadeKey(symbol);
    }

    /// <summary>
    /// Path → linked-view count across the entire solution (not a filtered
    /// sourceFile subset). Used so multi-view derived targets are still
    /// detected when the walk is limited to one source path.
    /// </summary>
    internal static Dictionary<string, int> BuildLinkedPathCounts(Solution solution)
    {
        var groups = AllFilesDocumentHelpers.GroupByLinkedPath(
            AllFilesDocumentHelpers.EnumerateCsharpDocuments(solution));
        return groups
            .Where(g => g.Count > 0 && g[0].FilePath != null)
            .GroupBy(g => PathResolver.GetPathComparisonKey(g[0].FilePath!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// True when any declaring document of <paramref name="type"/> shares a
    /// physical path with multiple linked workspace views.
    /// </summary>
    private static bool DeclaringPathHasLinkedMultiView(
        INamedTypeSymbol type,
        Solution solution,
        IReadOnlyDictionary<string, int> linkedPathCounts)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var document = solution.GetDocument(syntaxRef.SyntaxTree);
            if (document?.FilePath == null)
                continue;

            var pathKey = PathResolver.GetPathComparisonKey(document.FilePath);
            if (linkedPathCounts.TryGetValue(pathKey, out var count) && count > 1)
                return true;
        }

        return false;
    }

    private static List<Document> FilterAllFilesDocumentsBySourceFile(List<Document> documents, string sourceFile)
    {
        var normalizedSourceFile = PathResolver.NormalizePath(sourceFile);
        var exactMatches = documents
            .Where(d => string.Equals(PathResolver.NormalizePath(d.FilePath!), normalizedSourceFile, StringComparison.Ordinal))
            .ToList();
        if (exactMatches.Count > 0)
        {
            var exactKeys = exactMatches
                .Select(d => PathResolver.GetPathComparisonKey(d.FilePath!))
                .ToHashSet(StringComparer.Ordinal);
            return documents
                .Where(d => exactKeys.Contains(PathResolver.GetPathComparisonKey(d.FilePath!)))
                .ToList();
        }

        var matchedDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(documents, normalizedSourceFile);
        var distinctPaths = matchedDocuments
            .Select(d => PathResolver.GetPathComparisonKey(d.FilePath!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return distinctPaths.Count switch
        {
            0 when !File.Exists(sourceFile) => throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"Source file not found: {sourceFile}"),
            0 => throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"File not found in workspace: {sourceFile}"),
            > 1 => throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"Multiple workspace files match path ignoring case: {sourceFile}. Use the exact file path casing."),
            _ => matchedDocuments
        };
    }

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>TypeDeclarationSyntax</c> <c>FirstOrDefault</c> (enum and
    /// <c>DelegateDeclarationSyntax</c> do not participate) and
    /// line-only exclusive-end coverage (<see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// Do not force column 1 when omitted. Do not change
    /// omitted-line/omitted-column to <c>BaseTypeDeclarationSyntax</c>
    /// FirstOrDefault. Do not add enums or delegates to the omitted-line
    /// set. Column without line keeps today's first-match after the
    /// typeName filter (<c>TypeDeclarationSyntax</c> only) rather than
    /// substituting each candidate's own start line. When column is set
    /// with line, picks the type whose identifier or declaration span
    /// covers that 1-based column (same exclusive-end coverage as
    /// <c>SpanCoverage.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing type. Nested types,
    /// enums, and <c>DelegateDeclarationSyntax</c> participate when line
    /// is set so a covering enum or delegate still reaches
    /// <c>InvalidSymbolKind</c> rather than retargeting a later class. Do
    /// not require the declaration to start on <paramref name="line"/>
    /// when column is set — a split declaration may put the identifier on
    /// a continuation line. If column is set with line and nothing covers
    /// that position, return null (TypeNotFound) rather than falling back
    /// to first-match. After the push (source rewrite + members added to
    /// derived types), recover the selected type from the per-execution
    /// syntax annotation — do not reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null)
    {
        var simpleName = typeName.Contains('.')
            ? typeName[(typeName.LastIndexOf('.') + 1)..]
            : typeName;

        var typeCandidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == simpleName)
            .ToList();

        // Line set (including column+line) uses BaseTypeDeclarationSyntax
        // (enum) plus DelegateDeclarationSyntax so a covering enum or
        // delegate reaches InvalidSymbolKind rather than retargeting a
        // later class. Omitted-line / column-without-line stay
        // TypeDeclarationSyntax only — do not switch that set to
        // BaseTypeDeclarationSyntax (that would add enums).
        var lineCandidates = line.HasValue
            ? root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(t => t.Identifier.Text == simpleName)
                .Cast<MemberDeclarationSyntax>()
                .Concat(root.DescendantNodes()
                    .OfType<DelegateDeclarationSyntax>()
                    .Where(d => d.Identifier.Text == simpleName))
                .ToList()
            : typeCandidates.Cast<MemberDeclarationSyntax>().ToList();

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter
        // (TypeDeclarationSyntax only).
        if (column.HasValue && !line.HasValue)
            return typeCandidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Include enum and delegate candidates so a
            // covering enum or delegate still reaches InvalidSymbolKind.
            // Do not silently pick the first when a covering node exists
            // elsewhere — scan every candidate. If nothing covers this
            // position, keep today's not-found (null) rather than
            // inventing a first-match.
            return lineCandidates
                .Where(t => TypeCoverage.TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => TypeCoverage.IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return typeCandidates.FirstOrDefault();

        // Line set: include BaseTypeDeclarationSyntax (enum) and
        // DelegateDeclarationSyntax in the covering-line set. Do not
        // require the declaration to start on `line` — a split type's
        // identifier may live on a continuation line whose declaration
        // span still covers that line. Prefer the identifier hit, then
        // the smallest containing type (nested over outer). Include enum
        // and delegate candidates. Do not silently pick the first when a
        // covering node exists elsewhere — scan every candidate. If
        // nothing covers this line, keep today's TypeDeclarationSyntax
        // first-match rather than inventing a not-found (enums and
        // delegates stay out of that omitted-line fallback).
        if (lineCandidates.Count == 0)
            return null;

        return lineCandidates
            .Where(t => TypeCoverage.TypeCoversLine(t, line.Value))
            .OrderBy(t => TypeCoverage.IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? typeCandidates.FirstOrDefault();
    }

    private static TypeDeclarationSyntax RecoverAnnotatedType(
        SyntaxNode root,
        SyntaxAnnotation sourceTypeAnnotation,
        TypeDeclarationSyntax original,
        string typeName)
    {
        var annotated = root.GetAnnotatedNodes(sourceTypeAnnotation)
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (annotated != null)
            return annotated;

        return TypePartRematch.RematchTypeDeclaration(root, original)
            ?? throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found in file.");
    }




    /// <summary>
    /// Folds descendant derived-type replacements that
    /// <c>ReplaceNodes</c> already applied onto <paramref name="rewrittenAncestor"/>
    /// into the precomputed source rewrite. The source rewrite was built
    /// before those nested updates (and may use a different node identity
    /// after the per-execution annotation), so match nested types by
    /// identifier and enclosing type rather than <c>SpanStart</c>.
    /// </summary>
    private static TypeDeclarationSyntax MergeEnclosedDerivedUpdates(
        TypeDeclarationSyntax sourceReplacement,
        TypeDeclarationSyntax rewrittenAncestor,
        TypeDeclarationSyntax originalSource,
        IReadOnlyDictionary<SyntaxNode, SyntaxNode> map)
    {
        var enclosed = map.Keys
            .OfType<TypeDeclarationSyntax>()
            .Where(node => node != originalSource && originalSource.Contains(node))
            .ToList();
        if (enclosed.Count == 0)
            return sourceReplacement;

        var stillPresent = enclosed.Where(sourceReplacement.Contains).ToList();
        if (stillPresent.Count == enclosed.Count)
        {
            return (TypeDeclarationSyntax)sourceReplacement.ReplaceNodes(
                stillPresent,
                (nested, _) => map[nested]);
        }

        var transplants = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var nestedOriginal in enclosed)
        {
            var inReplacement = FindMatchingNestedType(sourceReplacement, nestedOriginal);
            if (inReplacement == null || transplants.ContainsKey(inReplacement))
                continue;

            var updated = TypePartRematch.RematchTypeDeclaration(rewrittenAncestor, nestedOriginal)
                ?? FindMatchingNestedType(rewrittenAncestor, nestedOriginal)
                ?? map[nestedOriginal] as TypeDeclarationSyntax;
            if (updated != null)
                transplants[inReplacement] = updated;
        }

        if (transplants.Count == 0)
            return sourceReplacement;

        return (TypeDeclarationSyntax)sourceReplacement.ReplaceNodes(
            transplants.Keys,
            (node, _) => transplants[node]);
    }

    private static TypeDeclarationSyntax? FindMatchingNestedType(
        TypeDeclarationSyntax container,
        TypeDeclarationSyntax original)
    {
        var bySpan = TypePartRematch.RematchTypeDeclaration(container, original);
        if (bySpan != null)
            return bySpan;

        var parentName = (original.Parent as TypeDeclarationSyntax)?.Identifier.Text;
        return container.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.Text == original.Identifier.Text)
            .Where(candidate =>
                (candidate.Parent as TypeDeclarationSyntax)?.Identifier.Text == parentName)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
    }

    private static IReadOnlyList<DerivedUpdate> RematchDerivedUpdates(
        SyntaxNode sourceRoot,
        Document sourceDocument,
        IReadOnlyList<DerivedUpdate> derivedUpdates)
    {
        var rematched = new List<DerivedUpdate>(derivedUpdates.Count);
        foreach (var update in derivedUpdates)
        {
            if (update.Original.SyntaxTree == sourceRoot.SyntaxTree)
            {
                rematched.Add(update);
                continue;
            }

            var updateDocument = sourceDocument.Project.Solution.GetDocument(update.Original.SyntaxTree)
                ?? DocumentForTreeHelpers.GetDocumentByFilePath(sourceDocument.Project.Solution, update.Original.SyntaxTree);
            if (updateDocument != null && updateDocument.Id == sourceDocument.Id)
            {
                var rematchedOriginal = TypePartRematch.RematchTypeDeclaration(sourceRoot, update.Original)
                    ?? throw new RefactoringException(
                        ErrorCodes.RoslynError,
                        $"Could not locate declaration for '{update.Type.Name}'.");
                rematched.Add(update with { Original = rematchedOriginal });
                continue;
            }

            rematched.Add(update);
        }

        return rematched;
    }

    internal static async Task<IReadOnlyList<INamedTypeSymbol>> GetDerivedTypes(
        INamedTypeSymbol source,
        IReadOnlyList<string>? targetNames,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var discovered = new List<INamedTypeSymbol>();

        if (source.TypeKind == TypeKind.Interface)
        {
            var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(
                source, solution, transitive: false, cancellationToken: cancellationToken);
            discovered.AddRange(derivedInterfaces);

            var implementations = await SymbolFinder.FindImplementationsAsync(
                source, solution, cancellationToken: cancellationToken);
            foreach (var implementation in implementations.OfType<INamedTypeSymbol>())
            {
                if (IsDirectInterface(implementation, source))
                    discovered.Add(implementation);
            }
        }
        else
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(
                source, solution, transitive: false, cancellationToken: cancellationToken);
            discovered.AddRange(derived);
        }

        var unique = DistinctTypes(discovered);

        if (targetNames == null || targetNames.Count == 0 || targetNames.All(string.IsNullOrWhiteSpace))
            return unique;

        var selected = new List<INamedTypeSymbol>();
        foreach (var name in targetNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var match = unique.FirstOrDefault(candidate =>
                candidate.Name.Equals(name, StringComparison.Ordinal) ||
                candidate.ToDisplayString().Equals(name, StringComparison.Ordinal));

            if (match == null)
            {
                throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"Type '{name}' is not a derived type of '{source.Name}'.");
            }

            selected.Add(match);
        }

        return DistinctTypes(selected);
    }

    private async Task<IReadOnlyList<INamedTypeSymbol>> GetDerivedTypes(
        INamedTypeSymbol source,
        IReadOnlyList<string>? targetNames,
        CancellationToken cancellationToken)
    {
        return await GetDerivedTypes(source, targetNames, Context.Solution, cancellationToken);
    }

    private static bool IsDirectInterface(INamedTypeSymbol type, INamedTypeSymbol iface)
    {
        return type.Interfaces.Any(implemented =>
            SymbolEqualityComparer.Default.Equals(implemented, iface) ||
            SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface.OriginalDefinition));
    }

    private static List<INamedTypeSymbol> DistinctTypes(IEnumerable<INamedTypeSymbol> types)
    {
        var unique = new List<INamedTypeSymbol>();
        foreach (var type in types)
        {
            if (unique.Any(existing => SymbolEqualityComparer.Default.Equals(existing, type)))
                continue;
            unique.Add(type);
        }

        return unique;
    }

    /// <summary>
    /// Rejects derived types that live only in metadata.
    /// </summary>
    internal static void ValidateDerivedIsEditable(INamedTypeSymbol derived)
    {
        if (!derived.Locations.Any(location => location.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.DerivedClassNotEditable,
                $"Derived type '{derived.Name}' is not editable (defined in an external assembly).");
        }
    }

    private static List<PushableMember> FindMembersToPush(
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<string> memberNames,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var requested = new HashSet<string>(memberNames.Where(n => !string.IsNullOrWhiteSpace(n)));
        var unmatched = new HashSet<string>(requested);
        var found = new List<PushableMember>();

        foreach (var (name, symbol, syntax) in EnumerateDeclaredMembers(typeDeclaration, semanticModel, cancellationToken))
        {
            if (symbol is IPropertySymbol { IsIndexer: true } indexer)
            {
                // Indexers match metadata name (Item), Roslyn name (this[]),
                // and conventional display (this[int i]) — same identity
                // forms as implement_interface / extract_interface /
                // extract_base_class / pull_members_up.
                if (!ImplementInterfaceOperation.MatchesRequestedMember(indexer, requested))
                    continue;

                if (!IsSupportedMember(indexer))
                {
                    throw new RefactoringException(
                        ErrorCodes.MemberNotMoveable,
                        $"Member '{indexer.Name}' cannot be pushed down.");
                }

                found.Add(new PushableMember(indexer.Name, indexer, syntax));
                foreach (var request in unmatched.ToList())
                {
                    if (ImplementInterfaceOperation.MatchesRequestedMember(
                            indexer, new HashSet<string> { request }))
                    {
                        unmatched.Remove(request);
                    }
                }

                continue;
            }

            if (!requested.Contains(name))
                continue;

            if (symbol == null)
            {
                throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    $"Could not resolve symbol for member '{name}'.");
            }

            if (!IsSupportedMember(symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"Member '{name}' cannot be pushed down.");
            }

            found.Add(new PushableMember(name, symbol, syntax));
            unmatched.Remove(name);
        }

        if (unmatched.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found: {string.Join(", ", unmatched)}");
        }

        return found;
    }

    private static IEnumerable<(string Name, ISymbol? Symbol, MemberDeclarationSyntax Syntax)> EnumerateDeclaredMembers(
        TypeDeclarationSyntax typeDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var member in typeDeclaration.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    yield return (method.Identifier.Text, semanticModel.GetDeclaredSymbol(method, cancellationToken), method);
                    break;
                case PropertyDeclarationSyntax property:
                    yield return (property.Identifier.Text, semanticModel.GetDeclaredSymbol(property, cancellationToken), property);
                    break;
                case IndexerDeclarationSyntax indexer:
                    yield return ("this[]", semanticModel.GetDeclaredSymbol(indexer, cancellationToken), indexer);
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), field);
                    }
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), eventField);
                    }
                    break;
                case EventDeclarationSyntax eventDecl:
                    yield return (eventDecl.Identifier.Text, semanticModel.GetDeclaredSymbol(eventDecl, cancellationToken), eventDecl);
                    break;
            }
        }
    }

    private static bool IsSupportedMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
        IPropertySymbol => true,
        IFieldSymbol => true,
        IEventSymbol => true,
        _ => false
    };

    private static void ValidateMembersForPush(
        IReadOnlyList<PushableMember> members,
        INamedTypeSymbol source,
        IReadOnlyList<INamedTypeSymbol> targets,
        bool leaveAbstract)
    {
        foreach (var member in members)
        {
            if (leaveAbstract && !CanBeAbstract(member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"Member '{member.Name}' cannot be left as abstract on '{source.Name}'.");
            }

            if (source.TypeKind != TypeKind.Interface &&
                !leaveAbstract &&
                ImplementsInterfaceMember(member.Symbol, source))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberRequiredByContract,
                    $"Member '{member.Name}' implements an interface contract on '{source.Name}' and cannot be removed.");
            }

            if (source.TypeKind != TypeKind.Interface &&
                !leaveAbstract &&
                IsRequiredByAbstractBase(member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberRequiredByContract,
                    $"Member '{member.Name}' is required by an abstract base contract and cannot be removed.");
            }

            foreach (var target in targets)
            {
                if (!CanMoveMember(MemberAsSeenFromTarget(member.Symbol, source, target), target))
                {
                    if (target.TypeKind == TypeKind.Interface && !IsInterfaceCompatible(member.Symbol))
                    {
                        throw new RefactoringException(
                            ErrorCodes.MemberNotInterfaceCompatible,
                            $"Member '{member.Name}' cannot be pushed to interface '{target.Name}'.");
                    }

                    throw new RefactoringException(
                        ErrorCodes.ConflictsWithExistingMember,
                        $"Member '{member.Name}' already exists in '{target.Name}'.");
                }
            }
        }

        ValidateNoPostSubstitutionCollisions(members, source, targets);
    }

    /// <summary>
    /// Ensures converted copies do not collide with each other after
    /// constructed-generic substitution under C# declaration identity
    /// (name + arity + parameter types/by-ref mode; not return type or
    /// <c>params</c>; ref/in/out share one by-ref mode).
    /// E.g. <c>string M(T)</c> + <c>int M(U)</c> onto <c>Root&lt;int,int&gt;</c>,
    /// or <c>M(params T[])</c> + <c>M(U[])</c>, both become duplicate
    /// <c>M(int)</c>/<c>M(int[])</c> → CS0111. Existing target members are
    /// already covered by <see cref="CanMoveMember"/>.
    /// </summary>
    private static void ValidateNoPostSubstitutionCollisions(
        IReadOnlyList<PushableMember> members,
        INamedTypeSymbol source,
        IReadOnlyList<INamedTypeSymbol> targets)
    {
        if (members.Count < 2)
            return;

        foreach (var target in targets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var key = DeclarationCollisionKeyForTarget(member.Symbol, source, target);
                // Partial definition + implementation share declaration identity
                // but are one legal method pair that must both land on the target.
                if (member.Symbol is IMethodSymbol method &&
                    (method.IsPartialDefinition || method.PartialDefinitionPart != null))
                {
                    key += method.IsPartialDefinition ? "|partial-def" : "|partial-impl";
                }

                if (!seen.Add(key))
                {
                    throw new RefactoringException(
                        ErrorCodes.ConflictsWithExistingMember,
                        $"Pushing members to '{target.Name}' would create duplicate '{member.Name}' after generic substitution.");
                }
            }
        }
    }

    /// <summary>
    /// C# declaration-identity key after constructed-base substitution.
    /// Methods: name + generic arity + parameter types with by-ref mode
    /// (no return type, no <c>params</c> spelling). Indexers: same without
    /// arity. Other members: metadata name.
    /// </summary>
    internal static string DeclarationCollisionKeyForTarget(
        ISymbol symbol,
        INamedTypeSymbol source,
        INamedTypeSymbol target)
    {
        var effective = symbol;
        var constructed = GetConstructedBase(source, target);
        if (constructed != null && constructed.TypeArguments.Length > 0)
        {
            foreach (var candidate in constructed.GetMembers(symbol.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(
                        candidate.OriginalDefinition, symbol.OriginalDefinition))
                {
                    effective = candidate;
                    break;
                }
            }
        }

        return DeclarationCollisionKey(effective);
    }

    private static string DeclarationCollisionKey(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method =>
            method.Name + "`" + method.TypeParameters.Length + "("
                + string.Join(", ", method.Parameters.Select(ParameterCollisionKey)) + ")",
        IPropertySymbol { IsIndexer: true } indexer =>
            "this[" + string.Join(", ", indexer.Parameters.Select(ParameterCollisionKey)) + "]",
        _ => symbol.Name
    };

    private static string ParameterCollisionKey(IParameterSymbol parameter)
    {
        // C# forbids overloads that differ only by ref/in/out (CS0663).
        // Distinguish by-value vs by-ref; treat Ref/Out/In as one mode.
        // The params modifier is not part of declaration identity.
        var prefix = parameter.RefKind == RefKind.None ? string.Empty : "ref ";

        return prefix + TypeCollisionKey(parameter.Type);
    }

    /// <summary>
    /// C# declaration-signature type identity. <c>dynamic</c> is erased to
    /// <c>object</c> (including inside arrays/generics) so
    /// <c>M(dynamic)</c> and <c>M(object)</c> collide as CS0111.
    /// </summary>
    private static string TypeCollisionKey(ITypeSymbol type)
    {
        // dynamic and System.Object share declaration-signature identity.
        if (type.TypeKind == TypeKind.Dynamic ||
            type.SpecialType == SpecialType.System_Object)
        {
            return "object";
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                var commas = array.Rank <= 1 ? string.Empty : new string(',', array.Rank - 1);
                return TypeCollisionKey(array.ElementType) + "[" + commas + "]";
            case IPointerTypeSymbol pointer:
                return TypeCollisionKey(pointer.PointedAtType) + "*";
            case INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } named
                when named.TypeArguments.Length > 0:
                var definition = named.OriginalDefinition.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat);
                var typeArgStart = definition.IndexOf('<');
                var head = typeArgStart >= 0 ? definition[..typeArgStart] : definition;
                var args = string.Join(", ", named.TypeArguments.Select(TypeCollisionKey));
                return head + "<" + args + ">";
            default:
                return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }

    private static void ValidateLeaveAbstractCoversConcreteDerived(
        INamedTypeSymbol source,
        IReadOnlyList<INamedTypeSymbol> allDerived,
        IReadOnlyList<INamedTypeSymbol> targets)
    {
        foreach (var derived in allDerived)
        {
            if (derived.IsAbstract)
                continue;

            if (WillHaveMemberAfterPush(derived, targets))
                continue;

            throw new RefactoringException(
                ErrorCodes.MemberNotMoveable,
                $"Cannot leave members abstract on '{source.Name}': derived type '{derived.Name}' would not receive an override.");
        }
    }

    private async Task ValidateNoBreakingReferencesAsync(
        IReadOnlyList<PushableMember> members,
        INamedTypeSymbol source,
        IReadOnlyList<INamedTypeSymbol> targets,
        bool leaveAbstract,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (source.TypeKind == TypeKind.Interface)
            return;

        if (leaveAbstract)
        {
            await ValidateEventsNotRaisedBySourceAsync(members, source, solution, cancellationToken);
            return;
        }

        foreach (var member in members)
        {
            var references = await SymbolFinder.FindReferencesAsync(
                member.Symbol, solution, cancellationToken);

            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.IsImplicit || location.Location.SourceTree == null)
                        continue;

                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    if (root == null || model == null)
                        continue;

                    var node = root.FindNode(location.Location.SourceSpan);

                    // Batch-internal refs (field + getter) may move together, but
                    // only when the receiver is implicit/this or already a type
                    // that will receive the member. Explicitly base-typed
                    // receivers (other.X) must still fail validation.
                    if (IsReferenceInsidePushBatch(location.Location, members))
                    {
                        if (BatchInternalReferenceIsSafe(node, model, targets))
                            continue;

                        throw new RefactoringException(
                            ErrorCodes.MemberRequiredByContract,
                            $"Cannot push '{member.Name}': it is still referenced through '{source.Name}' or a type that will not receive the member.");
                    }

                    var receiver = GetReceiverType(node, model);
                    if (WillHaveMemberAfterPush(receiver, targets))
                        continue;

                    throw new RefactoringException(
                        ErrorCodes.MemberRequiredByContract,
                        $"Cannot push '{member.Name}': it is still referenced through '{source.Name}' or a type that will not receive the member.");
                }
            }

            // SymbolFinder misses conditional indexer accesses (`other?[0]`).
            // Scan batch syntax for ElementBindingExpression under ConditionalAccess.
            await ValidateConditionalElementAccessesInBatchAsync(
                member, members, targets, solution, cancellationToken);
        }
    }

    /// <summary>
    /// Finds conditional indexer usages (<c>receiver?[i]</c>) inside the push
    /// batch that bind to <paramref name="member"/> — these are invisible to
    /// <c>SymbolFinder.FindReferencesAsync</c> — and rejects unsafe
    /// receivers the same way as ordinary element-access references.
    /// </summary>
    private async Task ValidateConditionalElementAccessesInBatchAsync(
        PushableMember member,
        IReadOnlyList<PushableMember> batch,
        IReadOnlyList<INamedTypeSymbol> targets,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (member.Symbol is not IPropertySymbol { IsIndexer: true })
            return;

        foreach (var batchMember in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var model = await GetSemanticModelForSyntaxAsync(
                solution, batchMember.Syntax, cancellationToken);
            if (model == null)
                continue;

            foreach (var conditional in batchMember.Syntax
                         .DescendantNodesAndSelf()
                         .OfType<ConditionalAccessExpressionSyntax>())
            {
                if (conditional.WhenNotNull is not ElementBindingExpressionSyntax binding)
                    continue;

                var bound = model.GetSymbolInfo(binding, cancellationToken).Symbol
                    ?? model.GetSymbolInfo(conditional, cancellationToken).Symbol;
                if (bound == null ||
                    !SymbolEqualityComparer.Default.Equals(
                        bound.OriginalDefinition, member.Symbol.OriginalDefinition))
                {
                    continue;
                }

                if (conditional.Expression is ThisExpressionSyntax)
                    continue;

                if (WillHaveMemberAfterPush(
                        model.GetTypeInfo(conditional.Expression, cancellationToken).Type,
                        targets))
                {
                    continue;
                }

                throw new RefactoringException(
                    ErrorCodes.MemberRequiredByContract,
                    $"Cannot push '{member.Name}': it is still referenced through a type that will not receive the member.");
            }
        }
    }


    /// <summary>
    /// True when <paramref name="location"/> falls inside the syntax of any
    /// member in the current push batch (candidate for co-moving with the
    /// referenced member). Callers must still run
    /// <see cref="BatchInternalReferenceIsSafe"/> so explicitly base-typed
    /// receivers are not exempted.
    /// </summary>
    private static bool IsReferenceInsidePushBatch(
        Location location,
        IReadOnlyList<PushableMember> members)
    {
        if (location.SourceTree == null)
            return false;

        foreach (var batchMember in members)
        {
            if (batchMember.Syntax.SyntaxTree == location.SourceTree &&
                batchMember.Syntax.Span.Contains(location.SourceSpan))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a batch-internal reference is safe to move with the batch:
    /// implicit <c>this</c>, explicit <c>this</c>, or a receiver that
    /// <see cref="WillHaveMemberAfterPush"/>. Explicit receivers typed as the
    /// source (e.g. <c>other.X</c> where <c>other</c> is the base) are not safe.
    /// </summary>
    private static bool BatchInternalReferenceIsSafe(
        SyntaxNode node,
        SemanticModel model,
        IReadOnlyList<INamedTypeSymbol> targets)
    {
        var name = node as SimpleNameSyntax ??
                   node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();

        if (name?.Parent is MemberAccessExpressionSyntax access && access.Name == name)
        {
            if (access.Expression is ThisExpressionSyntax)
                return true;

            return WillHaveMemberAfterPush(model.GetTypeInfo(access.Expression).Type, targets);
        }

        if (name?.Parent is MemberBindingExpressionSyntax &&
            name.Parent.Parent is ConditionalAccessExpressionSyntax conditional)
        {
            return WillHaveMemberAfterPush(model.GetTypeInfo(conditional.Expression).Type, targets);
        }

        // Object / with / nested member-initializer names look like simple
        // identifiers (`new Animal { X = 1 }`, `new Holder { Child = { X = 1 } }`)
        // but bind to the initialized object's type, not implicit this.
        if (name != null &&
            TryGetObjectOrWithInitializerReceiver(name, model, out var initializerReceiver))
        {
            return WillHaveMemberAfterPush(initializerReceiver, targets);
        }

        // Indexer element-access (`other[0]`) and initializer implicit
        // element-access (`new Animal { [0] = 1 }`) have no simple name
        // binding to the indexer — resolve their receivers explicitly.
        if (TryGetElementAccessReceiver(node, model, out var elementReceiver, out var isThisElement))
        {
            if (isThisElement)
                return true;

            return WillHaveMemberAfterPush(elementReceiver, targets);
        }

        // Property / recursive pattern designators (`other is { X: 1 }`) look
        // like simple names but bind to the matched type, not implicit this.
        // When the pattern is governed by `this` (`this is { X: 1 }`), the
        // receiver becomes the derived target after the move — same as this.X.
        if (name != null &&
            TryGetPropertyOrRecursivePatternReceiver(
                name, model, out var patternReceiver, out var isThisPattern))
        {
            if (isThisPattern)
                return true;

            return WillHaveMemberAfterPush(patternReceiver, targets);
        }

        // Simple name / implicit this — moves with the containing batch member.
        return true;
    }

    private static ITypeSymbol? GetReceiverType(SyntaxNode node, SemanticModel model)
    {
        var name = node as SimpleNameSyntax ??
                   node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();

        if (name?.Parent is MemberAccessExpressionSyntax access && access.Name == name)
            return model.GetTypeInfo(access.Expression).Type;

        if (name?.Parent is MemberBindingExpressionSyntax &&
            name.Parent.Parent is ConditionalAccessExpressionSyntax conditional)
        {
            return model.GetTypeInfo(conditional.Expression).Type;
        }

        if (name != null &&
            TryGetObjectOrWithInitializerReceiver(name, model, out var initializerReceiver))
        {
            return initializerReceiver;
        }

        if (TryGetElementAccessReceiver(node, model, out var elementReceiver, out _))
            return elementReceiver;

        if (name != null &&
            TryGetPropertyOrRecursivePatternReceiver(
                name, model, out var patternReceiver, out _))
        {
            return patternReceiver;
        }

        return model.GetEnclosingSymbol(node.SpanStart)?.ContainingType;
    }

    /// <summary>
    /// True when <paramref name="node"/> is (or sits under) an
    /// <see cref="ElementAccessExpressionSyntax"/> or initializer
    /// <see cref="ImplicitElementAccessSyntax"/>. Sets
    /// <paramref name="receiverType"/> to the indexed expression's type
    /// (or the initializer object type for implicit access).
    /// <paramref name="isThisReceiver"/> is true for <c>this[...]</c>.
    /// </summary>
    private static bool TryGetElementAccessReceiver(
        SyntaxNode node,
        SemanticModel model,
        out ITypeSymbol? receiverType,
        out bool isThisReceiver)
    {
        receiverType = null;
        isThisReceiver = false;

        // Walk the indexer binding spine (element access / binding / bracket
        // list / implicit access). Stop at non-indexer ArgumentSyntax so
        // identifiers used as ordinary call arguments are not misclassified;
        // keep walking through bracketed indexer args (other[X] / other?[X]).
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is MemberDeclarationSyntax)
                break;

            if (current is ArgumentSyntax &&
                current.Parent is not BracketedArgumentListSyntax)
            {
                break;
            }

            if (current is ElementAccessExpressionSyntax elementAccess)
            {
                if (elementAccess.Expression is ThisExpressionSyntax)
                {
                    isThisReceiver = true;
                    receiverType = model.GetTypeInfo(elementAccess.Expression).Type;
                    return true;
                }

                receiverType = model.GetTypeInfo(elementAccess.Expression).Type;
                return receiverType != null;
            }

            // Conditional indexer: other?[0] → ElementBindingExpression under
            // ConditionalAccessExpression (reference span may land on either).
            ConditionalAccessExpressionSyntax? conditionalIndexer = current switch
            {
                ElementBindingExpressionSyntax binding
                    when binding.Parent is ConditionalAccessExpressionSyntax c => c,
                ConditionalAccessExpressionSyntax c
                    when c.WhenNotNull is ElementBindingExpressionSyntax => c,
                _ => null
            };

            if (conditionalIndexer != null)
            {
                if (conditionalIndexer.Expression is ThisExpressionSyntax)
                {
                    isThisReceiver = true;
                    receiverType = model.GetTypeInfo(conditionalIndexer.Expression).Type;
                    return true;
                }

                receiverType = model.GetTypeInfo(conditionalIndexer.Expression).Type;
                return receiverType != null;
            }

            if (current is ImplicitElementAccessSyntax implicitAccess)
            {
                return TryGetImplicitElementAccessReceiver(implicitAccess, model, out receiverType);
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the object / with / nested initializer that owns an
    /// <see cref="ImplicitElementAccessSyntax"/> (<c>[0] = value</c>).
    /// </summary>
    private static bool TryGetImplicitElementAccessReceiver(
        ImplicitElementAccessSyntax access,
        SemanticModel model,
        out ITypeSymbol? receiverType)
    {
        receiverType = null;

        if (access.Parent is not AssignmentExpressionSyntax assignment ||
            assignment.Left != access ||
            assignment.Parent is not InitializerExpressionSyntax initializer)
        {
            return false;
        }

        switch (initializer.Parent)
        {
            case BaseObjectCreationExpressionSyntax creation:
                receiverType = model.GetTypeInfo(creation).Type;
                return receiverType != null;
            case WithExpressionSyntax withExpression:
                receiverType = model.GetTypeInfo(withExpression.Expression).Type;
                return receiverType != null;
            case AssignmentExpressionSyntax outer when outer.Right == initializer:
                // Nested: Holder { Child = { [0] = 1 } } — receiver is Child's type.
                var memberSymbol = model.GetSymbolInfo(outer.Left).Symbol;
                receiverType = memberSymbol switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => model.GetTypeInfo(outer.Left).Type
                };
                return receiverType != null;
            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> is a property/recursive-pattern
    /// designator (e.g. <c>X</c> in <c>other is { X: 1 }</c> or nested
    /// <c>Child: { X: 1 }</c>). Sets <paramref name="receiverType"/> to the
    /// type the pattern matches against (is/switch expression type, explicit
    /// pattern type, or outer subpattern member type).
    /// <paramref name="isThisReceiver"/> is true when that input comes from a
    /// governing <c>this</c> expression with no explicit pattern type (so after
    /// a batch move <c>this</c> denotes the derived target).
    /// </summary>
    private static bool TryGetPropertyOrRecursivePatternReceiver(
        SimpleNameSyntax name,
        SemanticModel model,
        out ITypeSymbol? receiverType,
        out bool isThisReceiver)
    {
        receiverType = null;
        isThisReceiver = false;

        // Standard designator: { X: pattern }
        if (name.Parent is NameColonSyntax nameColon &&
            nameColon.Name == name &&
            nameColon.Parent is SubpatternSyntax subpattern)
        {
            receiverType = GetPropertyPatternClauseInputType(
                subpattern, model, out isThisReceiver);
            return receiverType != null;
        }

        // Extended property pattern: { Child.X: pattern }
        foreach (var colon in name.Ancestors().OfType<ExpressionColonSyntax>())
        {
            if (colon.Parent is not SubpatternSyntax extendedSubpattern)
                continue;
            if (!colon.Expression.Span.Contains(name.Span))
                continue;

            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
            {
                // Extended path binds to the left of `.X`, never bare this.
                receiverType = model.GetTypeInfo(access.Expression).Type;
                isThisReceiver = false;
                return receiverType != null;
            }

            receiverType = GetPropertyPatternClauseInputType(
                extendedSubpattern, model, out isThisReceiver);
            return receiverType != null;
        }

        return false;
    }

    /// <summary>
    /// Input type for the <see cref="RecursivePatternSyntax"/> that owns
    /// <paramref name="subpattern"/>'s property-pattern clause.
    /// </summary>
    private static ITypeSymbol? GetPropertyPatternClauseInputType(
        SubpatternSyntax subpattern,
        SemanticModel model,
        out bool isThisReceiver)
    {
        isThisReceiver = false;
        if (subpattern.Parent is not PropertyPatternClauseSyntax clause ||
            clause.Parent is not RecursivePatternSyntax recursive)
        {
            return null;
        }

        return GetRecursivePatternInputType(recursive, model, out isThisReceiver);
    }

    /// <summary>
    /// Type a recursive/property pattern matches against: explicit pattern
    /// type, outer subpattern member type, or governing is/switch expression.
    /// <paramref name="isThisReceiver"/> is true only when the type comes from
    /// a bare governing <c>this</c> (no explicit pattern type, not nested).
    /// </summary>
    private static ITypeSymbol? GetRecursivePatternInputType(
        RecursivePatternSyntax recursive,
        SemanticModel model,
        out bool isThisReceiver)
    {
        isThisReceiver = false;

        if (recursive.Type != null)
        {
            var explicitType = model.GetTypeInfo(recursive.Type).Type;
            if (explicitType != null)
                return explicitType;
        }

        // Nested: parent Subpattern designator supplies the matched type
        // (`other is { Child: { X: 1 } }` → X matches Child's type).
        if (recursive.Parent is SubpatternSyntax nestedSubpattern)
        {
            if (nestedSubpattern.NameColon != null)
            {
                var member = model.GetSymbolInfo(nestedSubpattern.NameColon.Name).Symbol;
                return MemberType(member) ?? model.GetTypeInfo(nestedSubpattern.NameColon.Name).Type;
            }

            if (nestedSubpattern.ExpressionColon?.Expression is { } designator)
            {
                var member = model.GetSymbolInfo(designator).Symbol;
                return MemberType(member) ?? model.GetTypeInfo(designator).Type;
            }
        }

        // Walk past pattern wrappers (parentheses, unary not, binary and/or).
        PatternSyntax pattern = recursive;
        while (pattern.Parent is PatternSyntax parentPattern)
            pattern = parentPattern;

        switch (pattern.Parent)
        {
            case IsPatternExpressionSyntax isPattern:
                isThisReceiver = isPattern.Expression is ThisExpressionSyntax;
                return model.GetTypeInfo(isPattern.Expression).Type;
            case SwitchExpressionArmSyntax arm
                when arm.Parent is SwitchExpressionSyntax switchExpression:
                isThisReceiver = switchExpression.GoverningExpression is ThisExpressionSyntax;
                return model.GetTypeInfo(switchExpression.GoverningExpression).Type;
            case CasePatternSwitchLabelSyntax
                when pattern.Parent.Parent is SwitchSectionSyntax &&
                     pattern.Parent.Parent.Parent is SwitchStatementSyntax switchStatement:
                isThisReceiver = switchStatement.Expression is ThisExpressionSyntax;
                return model.GetTypeInfo(switchStatement.Expression).Type;
            default:
                return null;
        }
    }

    private static ITypeSymbol? MemberType(ISymbol? symbol) => symbol switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        IEventSymbol evt => evt.Type,
        _ => null
    };

    /// <summary>
    /// True when <paramref name="name"/> is the left-hand member of an object,
    /// <c>with</c>, or nested member initializer assignment. Sets
    /// <paramref name="receiverType"/> to the type that owns the member
    /// (creation type, with-source type, or nested member's type).
    /// </summary>
    private static bool TryGetObjectOrWithInitializerReceiver(
        SimpleNameSyntax name,
        SemanticModel model,
        out ITypeSymbol? receiverType)
    {
        receiverType = null;

        if (name.Parent is not AssignmentExpressionSyntax assignment ||
            assignment.Left != name ||
            assignment.Parent is not InitializerExpressionSyntax initializer)
        {
            return false;
        }

        switch (initializer.Parent)
        {
            case BaseObjectCreationExpressionSyntax creation:
                receiverType = model.GetTypeInfo(creation).Type;
                return receiverType != null;
            case WithExpressionSyntax withExpression:
                receiverType = model.GetTypeInfo(withExpression.Expression).Type;
                return receiverType != null;
            case AssignmentExpressionSyntax outer when outer.Right == initializer:
                // Nested member initializer: `new Holder { Child = { X = 1 } }`.
                // X is initialized on the type of Child, not on Holder / this.
                var memberSymbol = model.GetSymbolInfo(outer.Left).Symbol;
                receiverType = memberSymbol switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => model.GetTypeInfo(outer.Left).Type
                };
                return receiverType != null;
            default:
                return false;
        }
    }

    private static bool WillHaveMemberAfterPush(ITypeSymbol? receiver, IReadOnlyList<INamedTypeSymbol> targets)
    {
        if (receiver is not INamedTypeSymbol type)
            return false;

        for (var current = type; current != null; current = current.BaseType)
        {
            if (targets.Any(target =>
                    SymbolEqualityComparer.Default.Equals(target, current) ||
                    SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, current.OriginalDefinition)))
            {
                return true;
            }
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (targets.Any(target =>
                    SymbolEqualityComparer.Default.Equals(target, iface) ||
                    SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, iface.OriginalDefinition)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ValidateEventsNotRaisedBySourceAsync(
        IReadOnlyList<PushableMember> members,
        INamedTypeSymbol source,
        Solution solution,
        CancellationToken cancellationToken)
    {
        foreach (var member in members)
        {
            if (member.Symbol is not IEventSymbol)
                continue;

            var references = await SymbolFinder.FindReferencesAsync(
                member.Symbol, solution, cancellationToken);

            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.IsImplicit || location.Location.SourceTree == null)
                        continue;

                    if (member.Syntax.SyntaxTree == location.Location.SourceTree &&
                        member.Syntax.Span.Contains(location.Location.SourceSpan))
                    {
                        continue;
                    }

                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    if (root == null || model == null)
                        continue;

                    var node = root.FindNode(location.Location.SourceSpan);
                    if (!IsDeclaredIn(model.GetEnclosingSymbol(node.SpanStart), source))
                        continue;

                    if (!IsEventRaise(node))
                        continue;

                    throw new RefactoringException(
                        ErrorCodes.MemberNotMoveable,
                        $"Event '{member.Name}' cannot be left as abstract on '{source.Name}' because it is raised in that type.");
                }
            }
        }
    }

    private static bool IsDeclaredIn(ISymbol? symbol, INamedTypeSymbol source)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
             type != null;
             type = type.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, source) ||
                SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, source.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEventRaise(SyntaxNode node)
    {
        if (IsInAddOrRemoveAssignment(node))
            return false;

        for (var current = node; current != null && current is not MemberDeclarationSyntax; current = current.Parent)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation when InvocationRaisesEvent(invocation):
                    return true;
                case ConditionalAccessExpressionSyntax conditional when IsInvokeWhenNotNull(conditional.WhenNotNull):
                    return true;
            }
        }

        return false;
    }

    private static bool IsInAddOrRemoveAssignment(SyntaxNode node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is AssignmentExpressionSyntax assignment)
            {
                return assignment.IsKind(SyntaxKind.AddAssignmentExpression) ||
                       assignment.IsKind(SyntaxKind.SubtractAssignmentExpression);
            }
        }

        return false;
    }

    private static bool InvocationRaisesEvent(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            SimpleNameSyntax => true,
            MemberAccessExpressionSyntax access => access.Name.Identifier.Text == "Invoke",
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text == "Invoke",
            _ => false
        };
    }

    private static bool IsInvokeWhenNotNull(ExpressionSyntax whenNotNull) =>
        whenNotNull is InvocationExpressionSyntax invocation && InvocationRaisesEvent(invocation);

    /// <summary>
    /// For methods and indexers, returns the member as constructed on
    /// <paramref name="target"/>'s base / interface (so <c>M(T)</c> /
    /// <c>this[T]</c> on <c>Root&lt;T&gt;</c> is <c>M(int)</c> /
    /// <c>this[int]</c> when the target is <c>Root&lt;int&gt;</c>). Other
    /// members are unchanged.
    /// </summary>
    private static ISymbol MemberAsSeenFromTarget(ISymbol member, INamedTypeSymbol source, INamedTypeSymbol target)
    {
        if (member is not IMethodSymbol and not IPropertySymbol { IsIndexer: true })
            return member;

        var constructed = GetConstructedBase(source, target);
        if (constructed == null)
            return member;

        foreach (var candidate in constructed.GetMembers(member.Name))
        {
            if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, member.OriginalDefinition))
                return candidate;
        }

        return member;
    }

    /// <summary>
    /// Returns whether <paramref name="member"/> can be copied onto <paramref name="target"/>.
    /// </summary>
    internal static bool CanMoveMember(ISymbol member, INamedTypeSymbol target)
    {
        if (HierarchyConflictHelpers.HasConflict(target, member))
            return false;

        if (target.TypeKind == TypeKind.Interface && !IsInterfaceCompatible(member))
            return false;

        return true;
    }

    private static bool CanBeAbstract(ISymbol member) => member switch
    {
        // Partial methods cannot become abstract (abstract partial is illegal);
        // skip them when leaveAbstract so both definition + implementation parts
        // are not rewritten into invalid semicolon-only abstract partial decls.
        IMethodSymbol method => !method.IsStatic && !IsPartialMethodSymbol(method),
        IPropertySymbol property => !property.IsStatic && (!property.IsIndexer || CanPushIndexerAsAbstract(property)),
        IEventSymbol evt => !evt.IsStatic && evt.ExplicitInterfaceImplementations.Length == 0,
        _ => false
    };

    private static bool IsPartialMethodSymbol(IMethodSymbol method) =>
        method.IsPartialDefinition ||
        method.PartialDefinitionPart != null ||
        method.PartialImplementationPart != null;

    private static bool CanPushIndexerAsAbstract(IPropertySymbol indexer)
    {
        if (indexer.IsStatic || indexer.ExplicitInterfaceImplementations.Length > 0)
            return false;

        // A wholly private indexer is lifted to protected; implicit
        // accessors follow. An explicit private accessor on a more
        // visible indexer cannot become abstract (CS0621) and cannot
        // stay on the override if the base drops it (CS0546).
        if (indexer.DeclaredAccessibility == Accessibility.Private)
            return true;

        return indexer.GetMethod?.DeclaredAccessibility != Accessibility.Private
            && indexer.SetMethod?.DeclaredAccessibility != Accessibility.Private;
    }

    private static bool IsRequiredByAbstractBase(ISymbol member)
    {
        if (member is IMethodSymbol method && method.IsOverride)
        {
            for (var overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
            {
                if (overridden.IsAbstract)
                    return true;
            }
        }

        if (member is IPropertySymbol property && property.IsOverride)
        {
            for (var overridden = property.OverriddenProperty; overridden != null; overridden = overridden.OverriddenProperty)
            {
                if (overridden.IsAbstract)
                    return true;
            }
        }

        return false;
    }

    private static bool ImplementsInterfaceMember(ISymbol member, INamedTypeSymbol source)
    {
        // Explicit-interface indexers use a qualified name (IFoo.this[]),
        // so GetMembers(member.Name) on the interface would miss them.
        // Removing IFoo.this[...] from the source is CS0535; copying it
        // onto a derived type that does not re-list IFoo is CS0540.
        if (member is IPropertySymbol { IsIndexer: true, ExplicitInterfaceImplementations.Length: > 0 })
            return true;

        foreach (var iface in source.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(member.Name))
            {
                var implementation = source.FindImplementationForInterfaceMember(ifaceMember);
                if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, member))
                    return true;
            }
        }

        return false;
    }

    private static bool IsInterfaceCompatible(ISymbol member)
    {
        if (member.IsStatic)
            return false;

        if (member.DeclaredAccessibility != Accessibility.Public)
            return false;

        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
    }

    private static MemberDeclarationSyntax ConvertForDerived(
        PushableMember member,
        INamedTypeSymbol source,
        INamedTypeSymbol target,
        SemanticModel semanticModel,
        bool leaveAbstract)
    {
        var substituted = SubstituteTypeParameters(member.Syntax, semanticModel, source, target);
        var isolated = IsolateMemberSyntax(substituted, member.Name);

        if (target.TypeKind == TypeKind.Interface)
            return ConvertToInterfaceMember(isolated);

        var converted = leaveAbstract
            ? AddOverrideModifier(isolated, member.Symbol, target)
            : StripHierarchyModifiers(isolated);

        if (source.TypeKind == TypeKind.Interface && target.TypeKind != TypeKind.Interface)
            converted = EnsurePublicAccessibility(converted);

        return converted;
    }

    private static MemberDeclarationSyntax EnsurePublicAccessibility(MemberDeclarationSyntax member)
    {
        if (AccessibilityModifiers.HasAccessibility(member.Modifiers))
            return member;

        return member.WithModifiers(
            member.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
    }

    /// <summary>
    /// Keeps only the requested declarator when a field or event field declares
    /// multiple variables.
    /// </summary>
    internal static MemberDeclarationSyntax IsolateMemberSyntax(MemberDeclarationSyntax syntax, string name)
    {
        return syntax switch
        {
            FieldDeclarationSyntax field when field.Declaration.Variables.Count > 1 =>
                field.WithDeclaration(field.Declaration.WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        field.Declaration.Variables.First(v => v.Identifier.Text == name)))),
            EventFieldDeclarationSyntax eventField when eventField.Declaration.Variables.Count > 1 =>
                eventField.WithDeclaration(eventField.Declaration.WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        eventField.Declaration.Variables.First(v => v.Identifier.Text == name)))),
            _ => syntax
        };
    }

    private static MemberDeclarationSyntax SubstituteTypeParameters(
        MemberDeclarationSyntax member,
        SemanticModel semanticModel,
        INamedTypeSymbol source,
        INamedTypeSymbol target)
    {
        var constructed = GetConstructedBase(source, target);
        if (constructed == null || constructed.TypeArguments.Length == 0 || source.TypeParameters.Length == 0)
            return member;

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var identifier in member.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (replacements.ContainsKey(identifier))
                continue;

            if (semanticModel.GetSymbolInfo(identifier).Symbol is not ITypeParameterSymbol typeParameter)
                continue;

            if (typeParameter.ContainingSymbol is not INamedTypeSymbol containingType)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, source.OriginalDefinition))
                continue;

            var index = -1;
            for (var i = 0; i < source.TypeParameters.Length; i++)
            {
                if (source.TypeParameters[i].Name == typeParameter.Name)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0 || index >= constructed.TypeArguments.Length)
                continue;

            var replacement = SyntaxFactory
                .ParseTypeName(constructed.TypeArguments[index].ToDisplayString())
                .WithTriviaFrom(identifier);
            replacements[identifier] = replacement;
        }

        return replacements.Count == 0
            ? member
            : member.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
    }

    private static INamedTypeSymbol? GetConstructedBase(INamedTypeSymbol source, INamedTypeSymbol target)
    {
        for (var current = target.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, source.OriginalDefinition))
                return current;
        }

        return target.AllInterfaces.FirstOrDefault(iface =>
            SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, source.OriginalDefinition));
    }

    private static MemberDeclarationSyntax ConvertToInterfaceMember(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method when HasImplementationBody(method) => method
                .WithModifiers(SyntaxFactory.TokenList())
                .NormalizeWhitespace(),
            MethodDeclarationSyntax method => method
                .WithModifiers(SyntaxFactory.TokenList())
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => ToInterfaceProperty(property),
            IndexerDeclarationSyntax indexer => ToInterfaceIndexer(indexer),
            EventDeclarationSyntax eventDecl when eventDecl.AccessorList != null => eventDecl
                .WithModifiers(SyntaxFactory.TokenList())
                .NormalizeWhitespace(),
            EventDeclarationSyntax eventDecl => eventDecl
                .WithModifiers(SyntaxFactory.TokenList())
                .WithAccessorList(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField => SyntaxFactory.EventFieldDeclaration(eventField.Declaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            _ => throw new RefactoringException(
                ErrorCodes.MemberNotInterfaceCompatible,
                "Member cannot be declared on an interface.")
        };
    }

    private static bool HasImplementationBody(MethodDeclarationSyntax method) =>
        method.Body != null || method.ExpressionBody != null;

    private static PropertyDeclarationSyntax ToInterfaceProperty(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody != null ||
            (property.AccessorList?.Accessors.Any(accessor =>
                accessor.Body != null || accessor.ExpressionBody != null) ?? false))
        {
            return property
                .WithModifiers(SyntaxFactory.TokenList())
                .NormalizeWhitespace();
        }

        var accessors = new List<AccessorDeclarationSyntax>();
        if (property.AccessorList != null)
        {
            foreach (var accessor in property.AccessorList.Accessors)
            {
                accessors.Add(accessor
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return property
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static IndexerDeclarationSyntax ToInterfaceIndexer(IndexerDeclarationSyntax indexer)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (indexer.AccessorList != null)
        {
            foreach (var accessor in indexer.AccessorList.Accessors)
            {
                // Same public-accessor gate as extract_interface
                // CreateInterfaceIndexer / pull_members_up: a private /
                // protected / internal setter must not become a public
                // interface set;.
                if (AccessibilityModifiers.HasNonPublicAccessibility(accessor))
                    continue;

                accessors.Add(accessor
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (accessors.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotInterfaceCompatible,
                "Indexer cannot be pushed to an interface because it has no public accessors.");
        }

        return indexer
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExplicitInterfaceSpecifier(null)
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }


    private static MemberDeclarationSyntax ConvertToAbstract(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(ToAbstractModifiers(method.Modifiers))
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => ToAbstractProperty(property),
            IndexerDeclarationSyntax indexer when HierarchyAbstractEventIndexerHelpers.CanMakeIndexerAbstract(indexer) =>
                HierarchyAbstractEventIndexerHelpers.ToAbstractIndexer(indexer, ToAbstractModifiers(indexer.Modifiers)),
            EventDeclarationSyntax eventDecl when HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(eventDecl) =>
                HierarchyAbstractEventIndexerHelpers.ToAbstractEvent(eventDecl, ToAbstractModifiers(eventDecl.Modifiers)),
            EventFieldDeclarationSyntax eventField when HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(eventField) =>
                HierarchyAbstractEventIndexerHelpers.ToAbstractEvent(eventField, ToAbstractModifiers(eventField.Modifiers)),
            _ => throw new RefactoringException(
                ErrorCodes.MemberNotMoveable,
                "Only methods, properties, indexers, and events can be left as abstract members.")
        };
    }


    private static PropertyDeclarationSyntax ToAbstractProperty(PropertyDeclarationSyntax property)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (property.AccessorList != null)
        {
            foreach (var accessor in property.AccessorList.Accessors)
            {
                accessors.Add(accessor
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return property
            .WithModifiers(ToAbstractModifiers(property.Modifiers))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static MemberDeclarationSyntax AddOverrideModifier(
        MemberDeclarationSyntax member,
        ISymbol symbol,
        INamedTypeSymbol target)
    {
        return member switch
        {
            MethodDeclarationSyntax method => EnsureMethodBody(
                    ReduceOverrideAccessibility(
                        method.WithModifiers(ToOverrideModifiers(method.Modifiers)),
                        symbol,
                        target))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property =>
                ReduceOverrideAccessibility(
                        property.WithModifiers(ToOverrideModifiers(property.Modifiers)),
                        symbol,
                        target)
                    .NormalizeWhitespace(),
            IndexerDeclarationSyntax indexer when symbol is IPropertySymbol property =>
                EnsureIndexerBodies(
                    ReduceIndexerOverrideAccessibility(
                        indexer.WithModifiers(ToOverrideModifiers(indexer.Modifiers)),
                        property,
                        target))
                    .NormalizeWhitespace(),
            IndexerDeclarationSyntax indexer => EnsureIndexerBodies(
                    indexer.WithModifiers(ToOverrideModifiers(indexer.Modifiers)))
                .NormalizeWhitespace(),
            EventDeclarationSyntax eventDecl =>
                ReduceOverrideAccessibility(
                        eventDecl.WithModifiers(ToOverrideModifiers(eventDecl.Modifiers)),
                        symbol,
                        target)
                    .NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField =>
                ReduceOverrideAccessibility(
                        eventField.WithModifiers(ToOverrideModifiers(eventField.Modifiers)),
                        symbol,
                        target)
                    .NormalizeWhitespace(),
            _ => member.NormalizeWhitespace()
        };
    }

    /// <summary>
    /// Same-assembly: keep <c>protected internal</c>. Cross-assembly
    /// <c>protected internal</c> becomes <c>protected</c> (CS0507). Other
    /// accessibilities are unchanged.
    /// </summary>
    internal static SyntaxTokenList ReduceCrossAssemblyOverrideAccessibility(
        SyntaxTokenList modifiers,
        ISymbol member,
        INamedTypeSymbol target)
        => OverrideAccessibilityReducer.ReduceCrossAssemblyOverrideAccessibility(
            modifiers, member, target);

    /// <summary>
    /// Reduces member and accessor <c>protected internal</c> to
    /// <c>protected</c> when the override is emitted in another assembly.
    /// Methods, properties, events, and indexers share this path.
    /// </summary>
    internal static T ReduceOverrideAccessibility<T>(
        T member,
        ISymbol symbol,
        INamedTypeSymbol target)
        where T : MemberDeclarationSyntax
        => OverrideAccessibilityReducer.ReduceOverrideAccessibility(member, symbol, target);

    /// <summary>
    /// Reduces indexer and accessor <c>protected internal</c> to
    /// <c>protected</c> when the override is emitted in another assembly.
    /// </summary>
    internal static IndexerDeclarationSyntax ReduceIndexerOverrideAccessibility(
        IndexerDeclarationSyntax indexer,
        IPropertySymbol symbol,
        INamedTypeSymbol target)
        => OverrideAccessibilityReducer.ReduceIndexerOverrideAccessibility(
            indexer, symbol, target);

    private static MethodDeclarationSyntax EnsureMethodBody(MethodDeclarationSyntax method)
    {
        if (method.Body != null || method.ExpressionBody != null)
            return method;

        // Partial method declarations must stay semicolon-only; synthesizing a
        // body would turn the defining declaration into a second implementation.
        if (method.Modifiers.Any(SyntaxKind.PartialKeyword))
            return method;

        return method
            .WithSemicolonToken(default)
            .WithBody(CreateNotImplementedBlock());
    }

    /// <summary>
    /// Indexers cannot be auto-implemented. After <c>abstract</c> is stripped
    /// (or an abstract indexer is copied as <c>override</c>), bodyless
    /// accessors would be CS0501. Methods already get a throwing body.
    /// </summary>
    private static IndexerDeclarationSyntax EnsureIndexerBodies(IndexerDeclarationSyntax indexer)
    {
        if (indexer.ExpressionBody != null || indexer.AccessorList == null)
            return indexer;

        var changed = false;
        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var accessor in indexer.AccessorList.Accessors)
        {
            if (accessor.Body != null || accessor.ExpressionBody != null)
            {
                accessors.Add(accessor);
                continue;
            }

            changed = true;
            accessors.Add(accessor
                .WithSemicolonToken(default)
                .WithBody(CreateNotImplementedBlock()));
        }

        return changed
            ? indexer.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            : indexer;
    }

    private static BlockSyntax CreateNotImplementedBlock() =>
        SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("System.NotImplementedException"))
                    .WithArgumentList(SyntaxFactory.ArgumentList())));

    private static MemberDeclarationSyntax StripHierarchyModifiers(MemberDeclarationSyntax member)
    {
        // Keep virtual/override so further descendants continue to dispatch
        // and compile. Abstract is replaced with a body plus virtual.
        return member switch
        {
            MethodDeclarationSyntax method => EnsureMethodBody(
                    WithVirtualIfAbstract(method.WithModifiers(StripModifiers(
                        method.Modifiers,
                        SyntaxKind.AbstractKeyword,
                        SyntaxKind.NewKeyword,
                        SyntaxKind.SealedKeyword)),
                        method.Modifiers.Any(SyntaxKind.AbstractKeyword)))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property =>
                WithVirtualIfAbstract(property.WithModifiers(StripModifiers(
                    property.Modifiers,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.NewKeyword,
                    SyntaxKind.SealedKeyword)),
                    property.Modifiers.Any(SyntaxKind.AbstractKeyword))
                .NormalizeWhitespace(),
            IndexerDeclarationSyntax indexer =>
                EnsureIndexerBodies(
                    WithVirtualIfAbstract(indexer.WithModifiers(StripModifiers(
                        indexer.Modifiers,
                        SyntaxKind.AbstractKeyword,
                        SyntaxKind.NewKeyword,
                        SyntaxKind.SealedKeyword)),
                        indexer.Modifiers.Any(SyntaxKind.AbstractKeyword)))
                .NormalizeWhitespace(),
            FieldDeclarationSyntax field => field.NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField => eventField.NormalizeWhitespace(),
            EventDeclarationSyntax eventDecl => eventDecl
                .WithModifiers(StripModifiers(
                    eventDecl.Modifiers,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.NewKeyword,
                    SyntaxKind.SealedKeyword))
                .NormalizeWhitespace(),
            _ => member.NormalizeWhitespace()
        };
    }

    private static T WithVirtualIfAbstract<T>(T member, bool wasAbstract) where T : MemberDeclarationSyntax
    {
        if (!wasAbstract || member.Modifiers.Any(SyntaxKind.VirtualKeyword) || member.Modifiers.Any(SyntaxKind.OverrideKeyword))
            return member;

        return (T)member.AddModifiers(SyntaxFactory.Token(SyntaxKind.VirtualKeyword));
    }

    private static SyntaxTokenList ToAbstractModifiers(SyntaxTokenList modifiers)
    {
        var keepOverride = modifiers.Any(SyntaxKind.OverrideKeyword);
        var tokens = StripModifierKinds(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.SealedKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.NewKeyword,
                SyntaxKind.AsyncKeyword)
            .ToList();

        if (!AccessibilityModifiers.HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        if (keepOverride)
        {
            tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword)
                .WithTrailingTrivia(SyntaxFactory.ElasticSpace));
        }

        return SyntaxFactory.TokenList(tokens);
    }

    private static SyntaxTokenList ToOverrideModifiers(SyntaxTokenList modifiers)
    {
        var tokens = StripModifierKinds(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.NewKeyword,
                SyntaxKind.SealedKeyword)
            .ToList();

        if (modifiers.Any(SyntaxKind.PrivateKeyword) || !AccessibilityModifiers.HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword)
            .WithTrailingTrivia(SyntaxFactory.ElasticSpace));
        return SyntaxFactory.TokenList(tokens);
    }

    private static SyntaxTokenList StripModifiers(SyntaxTokenList modifiers, params SyntaxKind[] kinds) =>
        SyntaxFactory.TokenList(StripModifierKinds(modifiers, kinds));

    private static IEnumerable<SyntaxToken> StripModifierKinds(SyntaxTokenList modifiers, params SyntaxKind[] kinds)
    {
        var kindSet = kinds.ToHashSet();
        return modifiers.Where(token => !kindSet.Contains(token.Kind()));
    }


    private static TypeDeclarationSyntax BuildSourceReplacement(
        TypeDeclarationSyntax sourceDecl,
        IReadOnlyList<PushableMember> members,
        INamedTypeSymbol source,
        bool leaveAbstract)
    {
        if (source.TypeKind == TypeKind.Interface)
            return sourceDecl;

        var pushedNamesBySyntax = members
            .GroupBy(member => member.Syntax)
            .ToDictionary(group => group.Key, group => group.Select(member => member.Name).ToHashSet());

        var newMembers = new List<MemberDeclarationSyntax>();

        foreach (var member in sourceDecl.Members)
        {
            if (!pushedNamesBySyntax.TryGetValue(member, out var pushedNames))
            {
                newMembers.Add(member);
                continue;
            }

            if (TryKeepRemainingDeclarators(member, pushedNames, out var remaining))
            {
                newMembers.Add(remaining);
                if (leaveAbstract)
                {
                    foreach (var pushed in members.Where(m => m.Syntax == member))
                        newMembers.Add(ConvertToAbstract(IsolateMemberSyntax(member, pushed.Name)));
                }

                continue;
            }

            if (leaveAbstract)
            {
                foreach (var pushed in members.Where(m => m.Syntax == member))
                    newMembers.Add(ConvertToAbstract(IsolateMemberSyntax(member, pushed.Name)));
            }
        }

        var updated = sourceDecl.WithMembers(SyntaxFactory.List(newMembers));

        if (leaveAbstract &&
            sourceDecl is ClassDeclarationSyntax &&
            !sourceDecl.Modifiers.Any(SyntaxKind.AbstractKeyword))
        {
            updated = updated.AddModifiers(
                SyntaxFactory.Token(SyntaxKind.AbstractKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space));
        }

        return updated;
    }

    private static bool TryKeepRemainingDeclarators(
        MemberDeclarationSyntax member,
        HashSet<string> pushedNames,
        out MemberDeclarationSyntax remaining)
    {
        remaining = member;
        switch (member)
        {
            case FieldDeclarationSyntax field:
                {
                    var keep = field.Declaration.Variables
                        .Where(variable => !pushedNames.Contains(variable.Identifier.Text))
                        .ToList();
                    if (keep.Count == 0)
                        return false;

                    remaining = field.WithDeclaration(
                        field.Declaration.WithVariables(SyntaxFactory.SeparatedList(keep)));
                    return true;
                }
            case EventFieldDeclarationSyntax eventField:
                {
                    var keep = eventField.Declaration.Variables
                        .Where(variable => !pushedNames.Contains(variable.Identifier.Text))
                        .ToList();
                    if (keep.Count == 0)
                        return false;

                    remaining = eventField.WithDeclaration(
                        eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(keep)));
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Inserts a member into a type declaration.
    /// </summary>
    internal static TypeDeclarationSyntax AddMemberToType(TypeDeclarationSyntax typeDecl, MemberDeclarationSyntax member)
    {
        return AddMembersToType(typeDecl, [member]);
    }

    private static TypeDeclarationSyntax AddMembersToType(
        TypeDeclarationSyntax typeDecl,
        IReadOnlyList<MemberDeclarationSyntax> members)
    {
        var formatted = members.Select(member => member
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));

        var updated = typeDecl.WithMembers(typeDecl.Members.AddRange(formatted));
        // Partial methods are only legal on partial types.
        if (members.Any(IsPartialMethodSyntax))
            updated = EnsurePartialTypeModifier(updated);

        return updated;
    }

    private static bool IsPartialMethodSyntax(MemberDeclarationSyntax member) =>
        member is MethodDeclarationSyntax method &&
        method.Modifiers.Any(SyntaxKind.PartialKeyword);

    private static TypeDeclarationSyntax EnsurePartialTypeModifier(TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            return typeDecl;

        var partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        // Prefer `public partial class` over `public class partial`.
        for (var i = typeDecl.Modifiers.Count - 1; i >= 0; i--)
        {
            if (typeDecl.Modifiers[i].Kind() is SyntaxKind.PublicKeyword
                or SyntaxKind.PrivateKeyword
                or SyntaxKind.ProtectedKeyword
                or SyntaxKind.InternalKeyword
                or SyntaxKind.FileKeyword)
            {
                return typeDecl.WithModifiers(typeDecl.Modifiers.Insert(i + 1, partial));
            }
        }

        return typeDecl.WithModifiers(typeDecl.Modifiers.Insert(0, partial));
    }

    private static async Task<TypeDeclarationSyntax> GetTypeDeclarationAsync(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        var syntaxRef = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            throw new RefactoringException(
                ErrorCodes.DerivedClassNotEditable,
                $"Derived type '{type.Name}' is not editable (defined in an external assembly).");
        }

        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken) as TypeDeclarationSyntax;
        if (syntax == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate declaration for '{type.Name}'.");
        }

        return syntax;
    }

    private async Task<Solution> ApplyChangesAsync(
        Document sourceDocument,
        TypeDeclarationSyntax sourceDecl,
        TypeDeclarationSyntax newSource,
        IReadOnlyList<DerivedUpdate> derivedUpdates,
        CancellationToken cancellationToken)
    {
        return await ApplyChangesAsync(
            sourceDocument.Project.Solution,
            [(sourceDecl, newSource)],
            derivedUpdates,
            cancellationToken);
    }

    private async Task<Solution> ApplyChangesAsync(
        Solution solution,
        IReadOnlyList<(TypeDeclarationSyntax Original, TypeDeclarationSyntax Replacement)> sourceUpdates,
        IReadOnlyList<DerivedUpdate> derivedUpdates,
        CancellationToken cancellationToken)
    {
        var replacements = new List<(SyntaxTree Tree, SyntaxNode Original, SyntaxNode Replacement)>();
        foreach (var (original, replacement) in sourceUpdates)
            replacements.Add((original.SyntaxTree, original, replacement));

        foreach (var update in derivedUpdates)
            replacements.Add((update.Original.SyntaxTree, update.Original, update.Updated));

        var sourceOriginals = sourceUpdates
            .Select(update => (SyntaxNode)update.Original)
            .ToHashSet();

        foreach (var group in replacements.GroupBy(replacement => replacement.Tree))
        {
            // After WithSyntaxRoot (annotation), GetDocument(oldTree) can miss
            // when a derived type lives in the same file. Look up by file path
            // rather than treating the miss as a non-editable target.
            var document = solution.GetDocument(group.Key)
                ?? DocumentForTreeHelpers.GetDocumentByFilePath(solution, group.Key);
            if (document == null)
            {
                throw new RefactoringException(
                    ErrorCodes.DerivedClassNotEditable,
                    "A target type is not part of the workspace.");
            }

            var currentRoot = await document.GetSyntaxRootAsync(cancellationToken);
            if (currentRoot == null)
                throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var map = group.ToDictionary(item => item.Original, item => item.Replacement);
            // ReplaceNodes rewrites descendants first, then invokes the
            // ancestor callback with that rewritten node. Returning the
            // precomputed source replacement (and discarding `rewritten`)
            // drops a member added to a nested derived type that lives
            // inside the source declaration (e.g. Puppy : Animal inside
            // Animal). Merge those same-tree descendant updates into the
            // source rewrite.
            var newRoot = currentRoot.ReplaceNodes(map.Keys, (original, rewritten) =>
            {
                var replacement = map[original];
                if (!sourceOriginals.Contains(original))
                    return replacement;

                if (rewritten == original)
                    return replacement;

                return MergeEnclosedDerivedUpdates(
                    (TypeDeclarationSyntax)replacement,
                    (TypeDeclarationSyntax)rewritten,
                    (TypeDeclarationSyntax)original,
                    map);
            });
            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        return solution;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        PushMembersDownParams @params,
        INamedTypeSymbol source,
        IReadOnlyList<string> pushedNames,
        TypeDeclarationSyntax originalSource,
        TypeDeclarationSyntax updatedSource,
        IReadOnlyList<DerivedUpdate> derivedUpdates)
    {
        var memberList = string.Join(", ", pushedNames);
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = source.TypeKind == TypeKind.Interface
                    ? $"Keep {memberList} on {@params.TypeName!}"
                    : @params.LeaveAbstract
                        ? $"Leave {memberList} as abstract on {@params.TypeName!}"
                        : $"Remove {memberList} from {@params.TypeName!}",
                BeforeSnippet = originalSource.Identifier.Text,
                AfterSnippet = updatedSource.NormalizeWhitespace().ToFullString()
            }
        };

        foreach (var update in derivedUpdates)
        {
            var location = update.Type.Locations.FirstOrDefault(l => l.IsInSource);
            var file = location?.SourceTree?.FilePath ?? @params.SourceFile!;
            pendingChanges.Add(new PendingChange
            {
                File = file,
                ChangeType = ChangeKind.Modify,
                Description = $"Add {memberList} to {update.Type.Name}",
                BeforeSnippet = update.Original.Identifier.Text,
                AfterSnippet = update.Updated.NormalizeWhitespace().ToFullString()
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record PushableMember(string Name, ISymbol Symbol, MemberDeclarationSyntax Syntax);

    private sealed record DerivedUpdate(
        INamedTypeSymbol Type,
        TypeDeclarationSyntax Original,
        TypeDeclarationSyntax Updated);
}

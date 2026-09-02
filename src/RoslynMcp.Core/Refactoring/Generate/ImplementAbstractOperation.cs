using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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
/// Generates implementation stubs for unimplemented abstract members inherited
/// by a selected class. When <see cref="ImplementAbstractParams.ThrowNotImplemented"/>
/// is true (the default), placeholder bodies are
/// <c>throw new global::System.NotImplementedException();</c>.
/// When false, methods and getters use
/// <see cref="SyntaxGenerationHelper.CreateDefaultReturnBody"/> and
/// setters / init setters use empty blocks, except <c>ref</c> /
/// <c>ref readonly</c> methods and getters which still throw
/// (a default return is not a valid ref return).
/// Honors optional <c>line</c> / <c>column</c> to disambiguate same-named
/// types in one file (identifier preferred, then smallest containing type).
/// Omitted line keeps today's <c>BaseTypeDeclarationSyntax</c>
/// <c>FirstOrDefault</c> pick (enum participates; delegates do not —
/// <c>DelegateDeclarationSyntax</c> is not a <c>BaseTypeDeclarationSyntax</c>).
/// When line is set, a covering delegate is included so it reaches
/// <c>InvalidSymbolKind</c> rather than retargeting a later class.
/// Honors <c>replaceExisting</c> to include already-implemented abstract
/// members, remove those declarations (including across partials) by
/// signature, and insert a standard generated stub. <c>new</c> hiders,
/// explicit interface implementations, and non-override ordinary members
/// are never replaced.
/// Honors optional <c>allFiles</c> to walk every C# document (or the
/// optional single <c>sourceFile</c>) and implement missing abstract
/// members on every eligible <see cref="TypeDeclarationSyntax"/>.
/// </summary>
public sealed class ImplementAbstractOperation : RefactoringOperationBase<ImplementAbstractParams>
{
    /// <summary>
    /// Creates a new implement abstract operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public ImplementAbstractOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ImplementAbstractParams @params) => Validate(@params);

    /// <summary>
    /// Validates implement-abstract parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ImplementAbstractParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.TypeName) ||
                @params.Members != null ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with typeName, members, line, or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

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
        ImplementAbstractParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Optional line/column disambiguates same-named types. Omitted
        // column keeps today's BaseTypeDeclarationSyntax FirstOrDefault
        // pick (enum participates; DelegateDeclarationSyntax does not).
        // Line set also includes a covering delegate so it reaches
        // InvalidSymbolKind instead of retargeting a later class.
        var typeDecl = FindTypeDeclaration(
            root, @params.TypeName!, @params.Line, @params.Column, out var hadCandidates);

        if (typeDecl == null)
        {
            // Empty typeName set is today's SymbolNotFound, even when
            // column+line is supplied. A positional miss (name exists,
            // nothing covers that column+line) is TypeNotFound — do not
            // silently first-match.
            if (hadCandidates && @params.Column.HasValue && @params.Line.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"Type '{@params.TypeName}' not found in file.");
            }

            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No type named '{@params.TypeName}' found in the source file.");
        }

        if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) is not INamedTypeSymbol typeSymbol)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"Could not resolve a symbol for type '{@params.TypeName}'.");
        }

        ValidateTypeCanHostAbstractImplementations(typeSymbol);

        if (typeDecl is not TypeDeclarationSyntax hostTypeDecl)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for implement_abstract.");
        }

        // Missing abstract members, plus already-implemented ones when
        // replaceExisting is true. Skip property/event accessors — those
        // are implemented as part of the property / indexer stub.
        var eligibleMembers = CollectMembersToImplement(typeSymbol, @params.ReplaceExisting);

        // Filter to requested members if specified. Indexers match metadata
        // name ("Item"), Roslyn name ("this[]"), and conventional display
        // ("this[int i]") so callers can filter by any of those forms.
        if (@params.Members != null && @params.Members.Count > 0)
        {
            var requestedSet = new HashSet<string>(@params.Members, StringComparer.Ordinal);
            eligibleMembers = eligibleMembers.Where(m => MatchesRequestedMember(m, requestedSet)).ToList();
        }

        if (eligibleMembers.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoUnimplementedAbstractMembers,
                $"Type '{typeSymbol.Name}' has no unimplemented abstract members.");
        }

        var replacements = ResolveReplacements(typeSymbol, eligibleMembers, @params.ReplaceExisting);
        var membersToReplace = eligibleMembers.Where(m => replacements.ContainsKey(m)).ToList();
        var membersToGenerate = eligibleMembers.Where(m => !replacements.ContainsKey(m)).ToList();

        var implementations = GenerateImplementations(eligibleMembers, @params.ThrowNotImplemented, typeSymbol);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                membersToGenerate,
                membersToReplace,
                implementations,
                replacements,
                document.Project.Solution,
                cancellationToken);
        }

        var solution = await ApplyImplementationsToSolutionAsync(
            document.Project.Solution,
            document,
            hostTypeDecl,
            typeSymbol,
            implementations,
            replacements,
            @params.TypeName!,
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
                Name = @params.TypeName!,
                FullyQualifiedName = typeSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>GenerateConstructorOperation.ExecuteAllFilesAsync</c> /
    /// <c>InlineConstantOperation.ExecuteAllFilesAsync</c> /
    /// <c>MakeStaticOperation.ExecuteAllFilesAsync</c> /
    /// <c>MakeNonStaticOperation.ExecuteAllFilesAsync</c> /
    /// <c>EncapsulateFieldOperation.ExecuteAllFilesAsync</c>) and implements
    /// missing abstract members on every eligible
    /// <see cref="TypeDeclarationSyntax"/> (class / struct / record /
    /// record struct / interface, including nested — same node kind as
    /// today's host after <c>FindTypeDeclaration</c>). Optional
    /// <c>sourceFile</c> limits the walk to that one file. Interface,
    /// static, struct, enum, delegate, no-unimplemented, uneditable,
    /// <c>NameCollision</c>, and otherwise ineligible types are skipped
    /// rather than failing the walk. When a later rewrite conflicts with
    /// an earlier one, the later claim is skipped. When every type is a
    /// no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ImplementAbstractParams @params,
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
            allDocuments = FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var implementedCountByDoc = new Dictionary<DocumentId, int>();
        var processedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDocumentEditable(document, Context.Workspace))
                continue;

            while (true)
            {
                var currentDocument = currentSolution.GetDocument(document.Id);
                if (currentDocument == null || !IsDocumentEditable(currentDocument, Context.Workspace))
                    break;

                var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null)
                    break;

                Solution? updated = null;
                foreach (var typeDeclaration in CollectTypeDeclarations(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
                    if (typeSymbol == null)
                        continue;

                    var typeKey = TypeWalkKey(currentDocument.Project.Id, typeSymbol);
                    if (!processedTypes.Add(typeKey))
                        continue;

                    try
                    {
                        updated = await TryImplementOneAsync(
                            currentDocument,
                            typeDeclaration,
                            typeSymbol,
                            @params,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        // Skip interface / static / struct / enum /
                        // delegate / no-unimplemented / uneditable /
                        // NameCollision / InvalidSymbolKind rather than
                        // failing the walk.
                        updated = null;
                    }

                    if (updated != null)
                        break;
                }

                if (updated == null)
                    break;

                currentSolution = updated;
                implementedCountByDoc[document.Id] =
                    implementedCountByDoc.GetValueOrDefault(document.Id) + 1;
            }
        }

        var documentsToCompare = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

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
                var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
                var currentRoot = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                if (originalRoot == null || currentRoot == null)
                    continue;

                var span = originalRoot.GetLocation().GetLineSpan();
                var implementedCount = implementedCountByDoc.GetValueOrDefault(document.Id);
                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = implementedCount > 0
                        ? BuildAllFilesDescription(implementedCount)
                        : "Update abstract implementations generated in other files",
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
    /// De-dupes types within one project across rematches and partials.
    /// Includes <paramref name="projectId"/> so two projects that both
    /// declare <c>TestApp.Widget</c> are not collapsed onto one walk key.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, INamedTypeSymbol typeSymbol) =>
        TypeWalkKey(projectId, typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    /// <summary>
    /// Same key shape as <see cref="TypeWalkKey(ProjectId, INamedTypeSymbol)"/>
    /// for tests that do not have a compilation symbol.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, string fullyQualifiedTypeName) =>
        $"{projectId.Id:D}\0{fullyQualifiedTypeName}";

    /// <summary>
    /// Preview description for a file that implemented abstract members
    /// on <paramref name="implementedCount"/> types.
    /// </summary>
    internal static string BuildAllFilesDescription(int implementedCount) =>
        implementedCount == 1
            ? "Implement abstract members"
            : $"Implement abstract members on {implementedCount} types";

    /// <summary>
    /// Collects every <see cref="TypeDeclarationSyntax"/> in
    /// <paramref name="root"/> (class / struct / interface / record /
    /// record struct, including nested — same node kind as today's
    /// <see cref="TypeDeclarationSyntax"/> host after
    /// <c>FindTypeDeclaration</c>). Deterministic
    /// <c>SpanStart</c> then span-length order.
    /// </summary>
    internal static IReadOnlyList<TypeDeclarationSyntax> CollectTypeDeclarations(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .OrderBy(type => type.SpanStart)
            .ThenBy(type => type.Span.Length)
            .ToList();

    private async Task<Solution?> TryImplementOneAsync(
        Document document,
        TypeDeclarationSyntax hostTypeDecl,
        INamedTypeSymbol typeSymbol,
        ImplementAbstractParams @params,
        CancellationToken cancellationToken)
    {
        if (!IsDocumentEditable(document, Context.Workspace))
            return null;

        if (typeSymbol.IsStatic
            || typeSymbol.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface or TypeKind.Struct)
        {
            return null;
        }

        if (hostTypeDecl is InterfaceDeclarationSyntax)
            return null;

        List<ISymbol> eligibleMembers;
        try
        {
            eligibleMembers = CollectMembersToImplement(typeSymbol, @params.ReplaceExisting);
        }
        catch (RefactoringException)
        {
            return null;
        }

        if (eligibleMembers.Count == 0)
            return null;

        Dictionary<ISymbol, ISymbol> replacements;
        try
        {
            replacements = ResolveReplacements(typeSymbol, eligibleMembers, @params.ReplaceExisting);
        }
        catch (RefactoringException)
        {
            return null;
        }

        var implementations = GenerateImplementations(eligibleMembers, @params.ThrowNotImplemented, typeSymbol);
        if (implementations.Count == 0)
            return null;

        try
        {
            return await ApplyImplementationsToSolutionAsync(
                document.Project.Solution,
                document,
                hostTypeDecl,
                typeSymbol,
                implementations,
                replacements,
                typeSymbol.Name,
                cancellationToken);
        }
        catch (RefactoringException)
        {
            return null;
        }
    }

    private static async Task<Solution> ApplyImplementationsToSolutionAsync(
        Solution solution,
        Document document,
        TypeDeclarationSyntax hostTypeDecl,
        INamedTypeSymbol typeSymbol,
        List<MemberDeclarationSyntax> implementations,
        Dictionary<ISymbol, ISymbol> replacements,
        string typeName,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later replaceExisting on another type
        // would recover the stale node via FirstOrDefault.
        SyntaxAnnotation? targetTypeAnnotation = null;
        if (replacements.Count > 0)
        {
            // Annotate before the rewrite. Removing a member from an
            // earlier same-file partial shifts both SpanStart and the
            // physical line of a later selected partial — do not re-find
            // with those stale values. Today's FindTypeDeclaration(root,
            // typeName, preferredSpanStart) is not enough (it also still
            // scans TypeDeclarationSyntax only).
            targetTypeAnnotation = new SyntaxAnnotation("implement-abstract-target-type");
            root = root.ReplaceNode(
                hostTypeDecl,
                hostTypeDecl.WithAdditionalAnnotations(targetTypeAnnotation));
            document = document.WithSyntaxRoot(root);
            solution = document.Project.Solution;

            solution = await RemoveExistingImplementationsAcrossPartialsAsync(
                solution, typeSymbol, replacements.Values, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{typeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            hostTypeDecl = root.GetAnnotatedNodes(targetTypeAnnotation)
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault()
                ?? throw new RefactoringException(
                    ErrorCodes.SymbolNotFound,
                    $"No type named '{typeName}' found in the source file.");
        }

        var newTypeDeclaration = AddMembers(hostTypeDecl, implementations);
        // Strip the per-execution annotation so it does not linger in the
        // workspace after commit.
        if (targetTypeAnnotation != null)
            newTypeDeclaration = (TypeDeclarationSyntax)newTypeDeclaration.WithoutAnnotations(targetTypeAnnotation);
        var newRoot = root.ReplaceNode(hostTypeDecl, newTypeDeclaration);
        return document.WithSyntaxRoot(newRoot).Project.Solution;
    }

    private static List<Document> FilterDocumentsBySourceFile(List<Document> documents, string sourceFile)
    {
        string wanted;
        try
        {
            wanted = PathResolver.NormalizePath(sourceFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            wanted = sourceFile;
        }

        return documents
            .Where(d => string.Equals(
                PathResolver.NormalizePath(d.FilePath!),
                wanted,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Returns whether <paramref name="document"/> can receive source edits
    /// (skip not throw — same checks as sibling AllFiles operations).
    /// </summary>
    internal static bool IsDocumentEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
            return false;

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
            return false;

        return workspace.CanApplyChange(ApplyChangesKind.ChangeDocument);
    }

    internal static void ValidateTypeCanHostAbstractImplementations(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.IsStatic
            || typeSymbol.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface or TypeKind.Struct)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for implement_abstract.");
        }
    }

    /// <summary>
    /// Missing implementable abstract members (today's
    /// <see cref="MemberAnalyzer.GetUnimplementedAbstractMembers"/> plus
    /// <see cref="IsImplementableAbstractMember"/>). When
    /// <paramref name="replaceExisting"/> is true, already-implemented
    /// abstract members declared on this type that this tool would emit
    /// are added. <c>new</c> hiders do not count as implementations.
    /// </summary>
    internal static List<ISymbol> CollectMembersToImplement(
        INamedTypeSymbol typeSymbol,
        bool replaceExisting)
    {
        var result = new List<ISymbol>();

        foreach (var member in MemberAnalyzer.GetUnimplementedAbstractMembers(typeSymbol))
        {
            if (IsImplementableAbstractMember(member, typeSymbol))
                AddUnique(result, member);
        }

        if (!replaceExisting)
            return result;

        foreach (var member in GetExistingAbstractImplementationTargets(typeSymbol))
            AddUnique(result, member);

        return result;
    }

    private static void AddUnique(List<ISymbol> members, ISymbol member)
    {
        if (members.Any(existing => SignaturesMatch(existing, member)))
            return;

        members.Add(member);
    }

    /// <summary>
    /// Abstract base members this type already implements via an eligible
    /// override (not a <c>new</c> hider, explicit interface implementation,
    /// or ordinary non-override). Intermediate concrete overrides mean this
    /// tool would not emit the member, so those are skipped.
    /// </summary>
    private static IEnumerable<ISymbol> GetExistingAbstractImplementationTargets(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetMembers())
        {
            if (!IsEligibleExistingImplementation(member))
                continue;

            switch (member)
            {
                case IMethodSymbol method:
                    var methodTarget = GetImplementedAbstractMethod(method);
                    if (methodTarget != null)
                        yield return methodTarget;
                    break;
                case IPropertySymbol property:
                    var propertyTarget = GetImplementedAbstractProperty(property);
                    if (propertyTarget != null)
                        yield return propertyTarget;
                    break;
                case IEventSymbol evt:
                    var eventTarget = GetImplementedAbstractEvent(evt);
                    if (eventTarget != null)
                        yield return eventTarget;
                    break;
            }
        }
    }

    private static IMethodSymbol? GetImplementedAbstractMethod(IMethodSymbol method)
    {
        var current = method.OverriddenMethod;
        while (current != null)
        {
            if (current.IsAbstract && current.MethodKind == MethodKind.Ordinary)
                return current;
            if (!current.IsAbstract)
                return null;
            current = current.OverriddenMethod;
        }

        return null;
    }

    private static IPropertySymbol? GetImplementedAbstractProperty(IPropertySymbol property)
    {
        var current = property.OverriddenProperty;
        while (current != null)
        {
            if (current.IsAbstract)
                return current;
            if (!current.IsAbstract)
                return null;
            current = current.OverriddenProperty;
        }

        return null;
    }

    private static IEventSymbol? GetImplementedAbstractEvent(IEventSymbol evt)
    {
        var current = evt.OverriddenEvent;
        while (current != null)
        {
            if (current.IsAbstract)
                return current;
            if (!current.IsAbstract)
                return null;
            current = current.OverriddenEvent;
        }

        return null;
    }

    /// <summary>
    /// Maps each selected abstract member to the existing implementation
    /// on this type that will be removed. Exact signature match wins. Two
    /// same-name existing implementations with no exact match is
    /// <see cref="ErrorCodes.NameCollision"/>.
    /// </summary>
    internal static Dictionary<ISymbol, ISymbol> ResolveReplacements(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<ISymbol> selectedMembers,
        bool replaceExisting)
    {
        var replacements = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
        if (!replaceExisting)
            return replacements;

        foreach (var selected in selectedMembers)
        {
            var existing = FindExistingImplementation(typeSymbol, selected, out var ambiguous);
            if (ambiguous)
            {
                throw new RefactoringException(
                    ErrorCodes.NameCollision,
                    $"Multiple existing implementations named '{selected.Name}' and none matches the selected signature.");
            }

            if (existing != null)
                replacements[selected] = existing;
        }

        return replacements;
    }

    private static ISymbol? FindExistingImplementation(
        INamedTypeSymbol typeSymbol,
        ISymbol selected,
        out bool ambiguous)
    {
        ambiguous = false;
        var sameName = new List<ISymbol>();
        ISymbol? exact = null;

        foreach (var member in typeSymbol.GetMembers())
        {
            if (!IsEligibleExistingImplementation(member))
                continue;
            if (!NamesMatch(member, selected))
                continue;

            sameName.Add(member);
            if (SignaturesMatch(member, selected))
                exact = member;
        }

        if (exact != null)
            return exact;

        if (sameName.Count >= 2)
        {
            ambiguous = true;
            return null;
        }

        return null;
    }

    private static bool IsEligibleExistingImplementation(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
            return false;
        if (IsExplicitImplementation(member))
            return false;

        return member switch
        {
            IMethodSymbol method => method.IsOverride
                && method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol property => property.IsOverride,
            IEventSymbol evt => evt.IsOverride,
            _ => false
        };
    }

    private static bool IsExplicitImplementation(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0
                || method.MethodKind == MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };

    private static bool NamesMatch(ISymbol left, ISymbol right)
    {
        if (string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            return true;

        return left is IPropertySymbol { IsIndexer: true }
            && right is IPropertySymbol { IsIndexer: true }
            && (string.Equals(left.MetadataName, right.MetadataName, StringComparison.Ordinal)
                || string.Equals(left.MetadataName, "Item", StringComparison.Ordinal)
                || string.Equals(right.MetadataName, "Item", StringComparison.Ordinal));
    }

    internal static bool SignaturesMatch(ISymbol left, ISymbol right)
    {
        if (left is IMethodSymbol leftMethod && right is IMethodSymbol rightMethod)
            return MethodSignaturesMatch(leftMethod, rightMethod);

        if (left is IPropertySymbol leftProp && right is IPropertySymbol rightProp)
            return PropertySignaturesMatch(leftProp, rightProp);

        if (left is IEventSymbol leftEvent && right is IEventSymbol rightEvent)
            return string.Equals(leftEvent.Name, rightEvent.Name, StringComparison.Ordinal);

        return false;
    }

    private static bool MethodSignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            return false;
        if (left.Arity != right.Arity)
            return false;
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
            if (!ParameterTypesMatch(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Method type parameters are distinct symbols on the abstract vs
    /// implementation (<c>Base.M&lt;T&gt;(T)</c> vs <c>C.M&lt;T&gt;(T)</c>),
    /// so <see cref="SymbolEqualityComparer.Default"/> misses an exact match.
    /// Compare those by ordinal; recurse through constructed named types,
    /// arrays, pointers, tuples, and nullable wrappers so
    /// <c>List&lt;T&gt;</c> / <c>T[]</c> still match. Concrete / named types
    /// that <see cref="SymbolEqualityComparer.Default"/> already equates stay
    /// as today.
    /// </summary>
    private static bool ParameterTypesMatch(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left is ITypeParameterSymbol leftTp
            && leftTp.TypeParameterKind == TypeParameterKind.Method
            && right is ITypeParameterSymbol rightTp
            && rightTp.TypeParameterKind == TypeParameterKind.Method)
        {
            return leftTp.Ordinal == rightTp.Ordinal;
        }

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
            return NamedTypesMatch(leftNamed, rightNamed);

        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank
                && ParameterTypesMatch(leftArray.ElementType, rightArray.ElementType);
        }

        if (left is IPointerTypeSymbol leftPtr && right is IPointerTypeSymbol rightPtr)
            return ParameterTypesMatch(leftPtr.PointedAtType, rightPtr.PointedAtType);

        return false;
    }

    private static bool NamedTypesMatch(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        if (!SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition))
            return false;

        if (left.IsTupleType || right.IsTupleType)
        {
            if (left.TupleElements.Length != right.TupleElements.Length)
                return false;

            for (var i = 0; i < left.TupleElements.Length; i++)
            {
                if (!ParameterTypesMatch(left.TupleElements[i].Type, right.TupleElements[i].Type))
                    return false;
            }

            return true;
        }

        if (left.TypeArguments.Length != right.TypeArguments.Length)
            return false;

        for (var i = 0; i < left.TypeArguments.Length; i++)
        {
            if (!ParameterTypesMatch(left.TypeArguments[i], right.TypeArguments[i]))
                return false;
        }

        return true;
    }

    private static bool PropertySignaturesMatch(IPropertySymbol left, IPropertySymbol right)
    {
        if (left.IsIndexer != right.IsIndexer)
            return false;
        if (!left.IsIndexer)
            return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
            if (!ParameterTypesMatch(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Abstract methods, properties/indexers, and events that are visible
    /// for override from <paramref name="fromType"/>. Public / protected /
    /// protected-internal always; <c>internal</c> / <c>private protected</c>
    /// only in the same assembly — same gate as
    /// <see cref="MemberAnalyzer.IsAccessibleFrom"/> /
    /// <c>generate_overrides</c>.
    /// </summary>
    internal static bool IsImplementableAbstractMember(ISymbol member, INamedTypeSymbol fromType)
    {
        if (!MemberAnalyzer.IsAccessibleFrom(member, fromType))
            return false;

        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary && method.IsAbstract,
            IPropertySymbol property => property.IsAbstract,
            IEventSymbol evt => evt.IsAbstract,
            _ => false
        };
    }

    internal static bool MatchesRequestedMember(ISymbol member, HashSet<string> requested)
    {
        if (requested.Contains(member.Name))
            return true;

        if (member is not IPropertySymbol { IsIndexer: true } indexer)
            return false;

        var withNames = $"this[{string.Join(", ", indexer.Parameters.Select(FormatIndexerParameterDisplay))}]";
        var typesOnly = $"this[{string.Join(",", indexer.Parameters.Select(p => p.Type.ToDisplayString()))}]";
        var typesOnlySpaced = $"this[{string.Join(", ", indexer.Parameters.Select(p => p.Type.ToDisplayString()))}]";
        return requested.Contains(indexer.MetadataName)
            || requested.Contains(withNames)
            || requested.Contains(typesOnly)
            || requested.Contains(typesOnlySpaced);
    }

    private static string FormatIndexerParameterDisplay(IParameterSymbol parameter)
    {
        var type = parameter.Type.ToDisplayString();
        return parameter.RefKind switch
        {
            RefKind.Ref => $"ref {type} {parameter.Name}",
            RefKind.Out => $"out {type} {parameter.Name}",
            RefKind.In => $"in {type} {parameter.Name}",
            RefKind.RefReadOnlyParameter => $"ref readonly {type} {parameter.Name}",
            _ => $"{type} {parameter.Name}"
        };
    }

    private static List<MemberDeclarationSyntax> GenerateImplementations(
        List<ISymbol> members,
        bool throwNotImplemented,
        INamedTypeSymbol emittingType)
    {
        var implementations = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? impl = member switch
            {
                IMethodSymbol method => CreateMethodStub(method, emittingType, throwNotImplemented),
                IPropertySymbol { IsIndexer: true } indexer => CreateIndexerStub(indexer, emittingType, throwNotImplemented),
                IPropertySymbol property => CreatePropertyStub(property, emittingType, throwNotImplemented),
                IEventSymbol evt => CreateEventStub(evt, emittingType),
                _ => null
            };

            if (impl != null)
                implementations.Add(impl);
        }

        return implementations;
    }

    /// <summary>
    /// Creates an override method stub. When <paramref name="throwNotImplemented"/>
    /// is true, the body is
    /// <c>throw new global::System.NotImplementedException();</c>;
    /// otherwise it uses <see cref="SyntaxGenerationHelper.CreateDefaultReturnBody"/>.
    /// <c>ref</c> / <c>ref readonly</c> methods always throw — a default
    /// return is not a valid ref return (CS8156).
    /// </summary>
    internal static MethodDeclarationSyntax CreateMethodStub(
        IMethodSymbol method,
        INamedTypeSymbol emittingType,
        bool throwNotImplemented = true)
    {
        var parameters = method.Parameters.Select(CreateParameter);
        var body = RequiresThrowBody(method, throwNotImplemented)
            ? CreateThrowNotImplementedBody()
            : SyntaxGenerationHelper.CreateDefaultReturnBody(method.ReturnType);
        var methodDecl = SyntaxFactory.MethodDeclaration(
                CreateMemberType(method.ReturnType, method.ReturnsByRef, method.ReturnsByRefReadonly),
                method.Name)
            .WithModifiers(CreateOverrideModifiers(method, emittingType))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(body);

        if (method.TypeParameters.Length > 0)
        {
            methodDecl = methodDecl.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(
                    SyntaxFactory.SeparatedList(method.TypeParameters.Select(tp => SyntaxFactory.TypeParameter(tp.Name)))));
        }

        return methodDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an override property stub. When <paramref name="throwNotImplemented"/>
    /// is true, accessors throw
    /// <c>new global::System.NotImplementedException()</c>;
    /// otherwise getters use a default-return body and setters / init setters
    /// use an empty block. <c>ref</c> / <c>ref readonly</c> getters always throw.
    /// Preserves init setters, accessor-specific accessibility, and required.
    /// </summary>
    internal static PropertyDeclarationSyntax CreatePropertyStub(
        IPropertySymbol property,
        INamedTypeSymbol emittingType,
        bool throwNotImplemented = true)
    {
        return SyntaxFactory.PropertyDeclaration(
                CreateMemberType(property.Type, property.ReturnsByRef, property.ReturnsByRefReadonly),
                property.Name)
            .WithModifiers(CreateOverrideModifiers(property, emittingType, property.IsRequired))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(CreateAccessors(property, emittingType, throwNotImplemented))))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an override indexer stub. Accessor bodies follow the same
    /// <paramref name="throwNotImplemented"/> rules as properties.
    /// </summary>
    internal static IndexerDeclarationSyntax CreateIndexerStub(
        IPropertySymbol indexer,
        INamedTypeSymbol emittingType,
        bool throwNotImplemented = true)
    {
        var parameters = indexer.Parameters.Select(CreateParameter);
        return SyntaxFactory.IndexerDeclaration(
                CreateMemberType(indexer.Type, indexer.ReturnsByRef, indexer.ReturnsByRefReadonly))
            .WithModifiers(CreateOverrideModifiers(indexer, emittingType, indexer.IsRequired))
            .WithParameterList(SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(CreateAccessors(indexer, emittingType, throwNotImplemented))))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an override event stub with empty add/remove accessors —
    /// same body shape as <see cref="SyntaxGenerationHelper.CreateEventStub"/>,
    /// plus <c>override</c> and the abstract member's accessibility.
    /// Cross-assembly <c>protected internal</c> is emitted as
    /// <c>protected</c> (CS0507).
    /// </summary>
    internal static EventDeclarationSyntax CreateEventStub(IEventSymbol evt, INamedTypeSymbol emittingType)
    {
        var addAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.AddAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());
        var removeAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.RemoveAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());

        return SyntaxFactory.EventDeclaration(
                SyntaxFactory.ParseTypeName(evt.Type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space),
                evt.Name)
            .WithModifiers(CreateOverrideModifiers(evt, emittingType))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { addAccessor, removeAccessor })))
            .NormalizeWhitespace();
    }

    private static List<AccessorDeclarationSyntax> CreateAccessors(
        IPropertySymbol property,
        INamedTypeSymbol emittingType,
        bool throwNotImplemented)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        var propertyAccessibility = SyntaxGenerationHelper.OverrideAccessibility(property, emittingType);

        if (property.GetMethod != null)
        {
            accessors.Add(CreateAccessor(
                property.GetMethod,
                propertyAccessibility,
                emittingType,
                SyntaxKind.GetAccessorDeclaration,
                throwNotImplemented));
        }

        if (property.SetMethod != null)
        {
            var kind = property.SetMethod.IsInitOnly
                ? SyntaxKind.InitAccessorDeclaration
                : SyntaxKind.SetAccessorDeclaration;
            accessors.Add(CreateAccessor(
                property.SetMethod,
                propertyAccessibility,
                emittingType,
                kind,
                throwNotImplemented));
        }

        return accessors;
    }

    private static AccessorDeclarationSyntax CreateAccessor(
        IMethodSymbol accessor,
        Accessibility propertyAccessibility,
        INamedTypeSymbol emittingType,
        SyntaxKind kind,
        bool throwNotImplemented)
    {
        BlockSyntax body;
        if (kind == SyntaxKind.GetAccessorDeclaration
            ? RequiresThrowBody(accessor, throwNotImplemented)
            : throwNotImplemented)
        {
            body = CreateThrowNotImplementedBody();
        }
        else if (kind == SyntaxKind.GetAccessorDeclaration)
        {
            body = SyntaxGenerationHelper.CreateDefaultReturnBody(accessor.ReturnType);
        }
        else
        {
            body = SyntaxFactory.Block();
        }

        var declaration = SyntaxFactory.AccessorDeclaration(kind)
            .WithBody(body);

        var modifiers = CreateAccessorModifiers(
            SyntaxGenerationHelper.OverrideAccessibility(accessor, emittingType),
            propertyAccessibility);
        if (modifiers.Count > 0)
            declaration = declaration.WithModifiers(modifiers);

        return declaration;
    }

    private static bool RequiresThrowBody(IMethodSymbol method, bool throwNotImplemented)
        => throwNotImplemented || method.ReturnsByRef || method.ReturnsByRefReadonly;

    private static SyntaxTokenList CreateAccessorModifiers(
        Accessibility accessorAccessibility,
        Accessibility propertyAccessibility)
    {
        if (accessorAccessibility == Accessibility.NotApplicable
            || accessorAccessibility == propertyAccessibility)
        {
            return default;
        }

        return SyntaxFactory.TokenList(ParseAccessibility(accessorAccessibility));
    }

    private static TypeSyntax CreateMemberType(ITypeSymbol type, bool returnsByRef, bool returnsByRefReadonly)
    {
        var inner = SyntaxFactory.ParseTypeName(type.ToDisplayString());
        if (returnsByRefReadonly)
        {
            return SyntaxFactory.RefType(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    inner)
                .WithTrailingTrivia(SyntaxFactory.Space);
        }

        if (returnsByRef)
        {
            return SyntaxFactory.RefType(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    inner)
                .WithTrailingTrivia(SyntaxFactory.Space);
        }

        return inner.WithTrailingTrivia(SyntaxFactory.Space);
    }

    internal static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("global::System.NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }

    private static ParameterSyntax CreateParameter(IParameterSymbol parameter)
    {
        var syntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name))
            .WithType(SyntaxFactory.ParseTypeName(parameter.Type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space));

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

    /// <summary>
    /// Override modifiers using the inherited accessibility, reduced for
    /// CS0507 when <paramref name="member"/> is <c>protected internal</c>
    /// and lives in another assembly.
    /// </summary>
    private static SyntaxTokenList CreateOverrideModifiers(
        ISymbol member,
        INamedTypeSymbol emittingType,
        bool isRequired = false)
    {
        var tokens = new List<SyntaxToken>();
        tokens.AddRange(ParseAccessibility(
            SyntaxGenerationHelper.OverrideAccessibility(member, emittingType)));
        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        if (isRequired)
            tokens.Add(SyntaxFactory.Token(SyntaxKind.RequiredKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        return SyntaxFactory.TokenList(tokens);
    }

    private static IEnumerable<SyntaxToken> ParseAccessibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Protected => new[]
            {
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            Accessibility.Internal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            Accessibility.ProtectedOrInternal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            Accessibility.ProtectedAndInternal => new[]
            {
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            },
            _ => new[]
            {
                SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)
            }
        };
    }

    private static TypeDeclarationSyntax AddMembers(
        TypeDeclarationSyntax typeDeclaration,
        List<MemberDeclarationSyntax> newMembers)
    {
        var members = typeDeclaration.Members.ToList();

        foreach (var member in newMembers)
        {
            members.Add(member
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
        }

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    /// <summary>
    /// Removes matched implementation declarations from every partial that
    /// holds them. Match by span/kind, not SyntaxNode reference — same seam
    /// as implement_interface / generate_property replaceExisting.
    /// Uses <see cref="SyntaxRemoveOptions.KeepExteriorTrivia"/> and
    /// <see cref="SyntaxRemoveOptions.KeepDirectives"/> so a leading
    /// <c>#if</c> / <c>#region</c> on the removed member does not orphan
    /// a following <c>#endif</c> / <c>#endregion</c>.
    /// </summary>
    private static async Task<Solution> RemoveExistingImplementationsAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IEnumerable<ISymbol> existingImplementations,
        CancellationToken cancellationToken)
    {
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();
        var eventDeclaratorsByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int FieldStart, int DeclaratorStart)>>>();

        foreach (var existing in existingImplementations)
        {
            foreach (var reference in existing.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (TryGetEventFieldDeclarator(syntax, out var eventField, out var declarator)
                    && eventField.Parent is TypeDeclarationSyntax eventPart)
                {
                    if (eventField.Declaration.Variables.Count > 1)
                    {
                        AddKeyed(eventDeclaratorsByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                            (eventField.SpanStart, declarator.SpanStart));
                        continue;
                    }

                    AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                        (eventField.SpanStart, eventField.Span.End, eventField.Kind()));
                    continue;
                }

                var memberSyntax = AsRemovableMember(syntax);
                if (memberSyntax == null)
                    continue;
                if (memberSyntax.Parent is not TypeDeclarationSyntax part)
                    continue;

                AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, part.SpanStart,
                    (memberSyntax.SpanStart, memberSyntax.Span.End, memberSyntax.Kind()));
            }
        }

        var trees = membersByTreeAndPart.Keys
            .Concat(eventDeclaratorsByTreeAndPart.Keys)
            .Distinct()
            .ToList();

        foreach (var tree in trees)
        {
            var document = GetDocumentForTree(solution, tree, typeSymbol.Name);
            var treeRoot = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            membersByTreeAndPart.TryGetValue(tree, out var membersByPart);
            eventDeclaratorsByTreeAndPart.TryGetValue(tree, out var eventDeclaratorsByPart);

            var toRemove = new List<MemberDeclarationSyntax>();
            var eventFieldRewrites = new Dictionary<EventFieldDeclarationSyntax, EventFieldDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (!SameSyntaxTree(reference.SyntaxTree, tree))
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax originalPart)
                    continue;
                // The solution root may already carry a target-type
                // annotation (new tree). Rematch by span — annotation does
                // not change SpanStart — so RemoveNodes sees nodes from
                // this root and keeps the annotation on the selected type.
                var part = RematchTypeDeclaration(treeRoot, originalPart);
                if (part == null)
                    continue;

                HashSet<(int Start, int End, SyntaxKind Kind)>? memberKeys = null;
                HashSet<(int FieldStart, int DeclaratorStart)>? eventDeclaratorKeys = null;
                if (membersByPart != null)
                    membersByPart.TryGetValue(part.SpanStart, out memberKeys);
                if (eventDeclaratorsByPart != null)
                    eventDeclaratorsByPart.TryGetValue(part.SpanStart, out eventDeclaratorKeys);

                foreach (var member in part.Members)
                {
                    if (member is EventFieldDeclarationSyntax eventField
                        && eventDeclaratorKeys != null
                        && eventDeclaratorKeys.Count > 0)
                    {
                        var remaining = eventField.Declaration.Variables
                            .Where(v => !eventDeclaratorKeys.Contains((eventField.SpanStart, v.SpanStart)))
                            .ToList();
                        if (remaining.Count == eventField.Declaration.Variables.Count)
                        {
                            if (memberKeys != null
                                && memberKeys.Contains((eventField.SpanStart, eventField.Span.End, eventField.Kind())))
                            {
                                toRemove.Add(eventField);
                            }

                            continue;
                        }

                        if (remaining.Count == 0)
                        {
                            toRemove.Add(eventField);
                            continue;
                        }

                        eventFieldRewrites[eventField] = eventField.WithDeclaration(
                            eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(remaining)));
                        continue;
                    }

                    if (memberKeys != null
                        && memberKeys.Contains((member.SpanStart, member.Span.End, member.Kind())))
                    {
                        toRemove.Add(member);
                    }
                }
            }

            SyntaxNode newRoot = treeRoot;
            if (eventFieldRewrites.Count > 0)
                newRoot = newRoot.ReplaceNodes(eventFieldRewrites.Keys, (original, _) => eventFieldRewrites[original]);

            if (toRemove.Count > 0)
            {
                // KeepDirectives / KeepExteriorTrivia so a leading #if / #region on
                // the removed member does not orphan a following #endif / #endregion.
                newRoot = newRoot.RemoveNodes(
                        toRemove,
                        SyntaxRemoveOptions.KeepExteriorTrivia | SyntaxRemoveOptions.KeepDirectives)
                    ?? newRoot;
            }

            if (eventFieldRewrites.Count == 0 && toRemove.Count == 0)
                continue;

            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    private static Document GetDocumentForTree(Solution solution, SyntaxTree tree, string typeName)
    {
        var document = solution.GetDocument(tree);
        if (document != null)
            return document;

        if (!string.IsNullOrEmpty(tree.FilePath))
        {
            foreach (var id in solution.GetDocumentIdsWithFilePath(tree.FilePath))
            {
                document = solution.GetDocument(id);
                if (document != null)
                    return document;
            }
        }

        throw new RefactoringException(
            ErrorCodes.DocumentNotEditable,
            $"Could not locate a declaring document for type '{typeName}'.");
    }

    private static bool SameSyntaxTree(SyntaxTree left, SyntaxTree right) =>
        left == right
        || (!string.IsNullOrEmpty(left.FilePath)
            && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase));

    private static TypeDeclarationSyntax? RematchTypeDeclaration(SyntaxNode root, TypeDeclarationSyntax original) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.SpanStart == original.SpanStart && t.Identifier.Text == original.Identifier.Text);

    private static void AddKeyed<T>(
        Dictionary<SyntaxTree, Dictionary<int, HashSet<T>>> map,
        SyntaxTree tree,
        int partSpanStart,
        T key)
    {
        if (!map.TryGetValue(tree, out var byPart))
        {
            byPart = new Dictionary<int, HashSet<T>>();
            map[tree] = byPart;
        }

        if (!byPart.TryGetValue(partSpanStart, out var keys))
        {
            keys = new HashSet<T>();
            byPart[partSpanStart] = keys;
        }

        keys.Add(key);
    }

    private static bool TryGetEventFieldDeclarator(
        SyntaxNode syntax,
        out EventFieldDeclarationSyntax eventField,
        out VariableDeclaratorSyntax declarator)
    {
        if (syntax is VariableDeclaratorSyntax variable
            && variable.Parent?.Parent is EventFieldDeclarationSyntax field)
        {
            eventField = field;
            declarator = variable;
            return true;
        }

        eventField = null!;
        declarator = null!;
        return false;
    }

    private static MemberDeclarationSyntax? AsRemovableMember(SyntaxNode syntax)
    {
        if (syntax is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
            or EventDeclarationSyntax or EventFieldDeclarationSyntax)
        {
            return (MemberDeclarationSyntax)syntax;
        }

        var ancestor = syntax.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        return ancestor is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
            or EventDeclarationSyntax or EventFieldDeclarationSyntax
            ? ancestor
            : null;
    }

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>BaseTypeDeclarationSyntax</c> <c>FirstOrDefault</c> (enum
    /// participates; <c>DelegateDeclarationSyntax</c> does not) and
    /// line-only exclusive-end coverage (<see cref="SpanCoversLine"/>).
    /// Do not force column 1 when omitted. Do not change
    /// omitted-line/omitted-column to <c>TypeDeclarationSyntax</c> /
    /// <c>ClassDeclarationSyntax</c> FirstOrDefault. Do not add delegates
    /// to the omitted-line set. Column without line keeps today's
    /// first-match after the typeName filter (BaseTypeDeclarationSyntax
    /// only) rather than substituting each candidate's own start line.
    /// When column is set with line, picks the type whose identifier or
    /// declaration span covers that 1-based column (same exclusive-end
    /// coverage as <c>GeneratePropertyOperation.SpanCoversColumn</c> /
    /// <c>GenerateOverridesOperation.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing type. Nested types,
    /// enums, and <c>DelegateDeclarationSyntax</c> participate when line
    /// is set so a covering enum or delegate still reaches
    /// <c>InvalidSymbolKind</c> rather than retargeting a later class. Do
    /// not require the declaration to start on <paramref name="line"/>
    /// when column is set — a split declaration may put the identifier on
    /// a continuation line. If column is set with line and nothing covers
    /// that position, return null (TypeNotFound) rather than falling back
    /// to first-match. An empty typeName candidate set also returns null
    /// (<c>hadCandidates</c> false → SymbolNotFound). After
    /// <see cref="RemoveExistingImplementationsAcrossPartialsAsync"/>, recover
    /// the selected type from the per-execution syntax annotation — do not
    /// reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null) =>
        FindTypeDeclaration(root, typeName, line, column, out _);

    /// <inheritdoc cref="FindTypeDeclaration(SyntaxNode, string, int?, int?)"/>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column,
        out bool hadCandidates)
    {
        var typeCandidates = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        // Line set (including column+line) also includes
        // DelegateDeclarationSyntax so a covering delegate reaches
        // InvalidSymbolKind rather than retargeting a later class.
        // Omitted-line / column-without-line stay BaseType only.
        var includeDelegates = line.HasValue;
        var lineCandidates = typeCandidates
            .Cast<MemberDeclarationSyntax>()
            .Concat(includeDelegates
                ? root.DescendantNodes()
                    .OfType<DelegateDeclarationSyntax>()
                    .Where(d => d.Identifier.Text == typeName)
                : Enumerable.Empty<MemberDeclarationSyntax>())
            .ToList();

        // Empty typeName set is today's missing-name (hadCandidates
        // false). When line is set, a delegate-only name is present.
        hadCandidates = includeDelegates
            ? lineCandidates.Count > 0
            : typeCandidates.Count > 0;

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter
        // (BaseTypeDeclarationSyntax only).
        if (column.HasValue && !line.HasValue)
            return typeCandidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Include enum and delegate candidates so a
            // covering delegate still reaches InvalidSymbolKind. Do not
            // silently pick the first when a covering node exists
            // elsewhere — scan every candidate. If nothing covers this
            // position, keep today's not-found (null) rather than
            // inventing a first-match.
            return lineCandidates
                .Where(t => TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return typeCandidates.FirstOrDefault();

        // Line set: include DelegateDeclarationSyntax in the covering-line
        // set. Do not require the declaration to start on `line` — a split
        // type's identifier may live on a continuation line whose
        // declaration span still covers that line. Prefer the identifier
        // hit, then the smallest containing type (nested over outer).
        // Include enum and delegate candidates. Do not silently pick the
        // first when a covering node exists elsewhere — scan every
        // candidate. If nothing covers this line, keep today's
        // BaseTypeDeclarationSyntax first-match rather than inventing a
        // not-found (delegates stay out of that omitted-line fallback).
        if (lineCandidates.Count == 0)
            return null;

        return lineCandidates
            .Where(t => TypeCoversLine(t, line.Value))
            .OrderBy(t => IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? typeCandidates.FirstOrDefault();
    }

    private static bool TypeCoversLine(MemberDeclarationSyntax type, int line) =>
        IdentifierCoversLine(type, line) ||
        SpanCoversLine(type.GetLocation().GetLineSpan(), line);

    private static bool IdentifierCoversLine(MemberDeclarationSyntax type, int line)
    {
        var identifier = GetTypeIdentifier(type);
        return identifier != default
            && SpanCoversLine(identifier.GetLocation().GetLineSpan(), line);
    }

    private static bool TypeCoversColumn(MemberDeclarationSyntax type, int line, int column) =>
        IdentifierCoversColumn(type, line, column) ||
        SpanCoversColumn(type.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(MemberDeclarationSyntax type, int line, int column)
    {
        var identifier = GetTypeIdentifier(type);
        return identifier != default
            && SpanCoversColumn(identifier.GetLocation().GetLineSpan(), line, column);
    }

    private static SyntaxToken GetTypeIdentifier(MemberDeclarationSyntax type) => type switch
    {
        BaseTypeDeclarationSyntax named => named.Identifier,
        DelegateDeclarationSyntax del => del.Identifier,
        _ => default
    };

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent type also
    /// match the previous declaration. Same helper as
    /// <c>GeneratePropertyOperation.SpanCoversColumn</c> /
    /// <c>GenerateOverridesOperation.SpanCoversColumn</c>.
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

    /// <summary>
    /// 1-based line coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so a span that ends at the start of a line does not
    /// cover that line. Treating the end as inclusive would let the first
    /// line of an adjacent type also match the previous declaration. Same
    /// exclusive-end idea as <c>GeneratePropertyOperation.SpanCoversLine</c>.
    /// </summary>
    internal static bool SpanCoversLine(FileLinePositionSpan span, int line)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == endLine && span.EndLinePosition.Character == 0)
            return false;
        return true;
    }

    /// <summary>
    /// Creates a preview result describing generate vs replace.
    /// When an existing member lives in another partial, also includes a
    /// Modify pending change per distinct declaring file with that member
    /// as <c>BeforeSnippet</c> — same as implement_interface /
    /// generate_property replaceExisting preview.
    /// </summary>
    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        ImplementAbstractParams @params,
        List<ISymbol> membersToGenerate,
        List<ISymbol> membersToReplace,
        List<MemberDeclarationSyntax> implementations,
        Dictionary<ISymbol, ISymbol> replacements,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var selectedMembers = membersToGenerate.Concat(membersToReplace).ToList();
        var throwNote = @params.ThrowNotImplemented
            ? "stubs will throw NotImplementedException"
            : "stubs will not throw";
        var description = membersToReplace.Count > 0 || @params.ReplaceExisting
            ? $"{BuildPreviewDescription(membersToGenerate, membersToReplace)} ({throwNote})"
            : $"Implement abstract members on '{@params.TypeName}': {string.Join(", ", selectedMembers.Select(m => m.Name))} ({throwNote})";
        var implCode = string.Join("\n\n",
            implementations.Select(i => i.NormalizeWhitespace().ToFullString()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = description,
                BeforeSnippet = membersToReplace.Count > 0
                    ? $"// Type '{@params.TypeName}' (replacing existing abstract members)"
                    : $"// End of type '{@params.TypeName}'",
                AfterSnippet = implCode
            }
        };

        if (replacements.Count > 0)
        {
            var sourcePath = PathResolver.NormalizePath(@params.SourceFile!);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in replacements.Values)
            {
                foreach (var reference in existing.DeclaringSyntaxReferences)
                {
                    var syntax = await reference.GetSyntaxAsync(cancellationToken);
                    var memberSyntax = AsRemovableMember(syntax);
                    if (memberSyntax == null)
                        continue;

                    var declaringDocument = solution.GetDocument(syntax.SyntaxTree);
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
                        Description = $"Remove existing {DescribeMemberKind(existing)} '{existing.Name}' from {@params.TypeName}",
                        BeforeSnippet = memberSyntax.NormalizeWhitespace().ToFullString(),
                        AfterSnippet = $"// {DescribeMemberKind(existing)} removed"
                    });
                }
            }
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    internal static string BuildPreviewDescription(
        IReadOnlyList<ISymbol> membersToGenerate,
        IReadOnlyList<ISymbol> membersToReplace)
    {
        var generated = string.Join(", ", membersToGenerate.Select(m => m.Name));
        var replaced = string.Join(", ", membersToReplace.Select(m => m.Name));

        if (membersToReplace.Count == 0)
            return $"Generate abstract members: {generated}";
        if (membersToGenerate.Count == 0)
            return $"Replace existing abstract members: {replaced}";
        return $"Generate abstract members: {generated}; replace existing abstract members: {replaced}";
    }

    private static string DescribeMemberKind(ISymbol member) => member switch
    {
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => "member"
    };
}

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
/// Deletes a selected symbol only when it has no remaining references.
/// Remaining usages are reported as an error with locations; no force-delete
/// or reference cleanup is performed. Optional <c>allFiles</c> walks every
/// C# document (or the optional single <c>sourceFile</c>) and deletes every
/// eligible unused declaration under today's single-site rules, skipping
/// symbols with usages rather than throwing.
/// </summary>
public sealed class SafeDeleteOperation : RefactoringOperationBase<SafeDeleteParams>
{
    /// <summary>
    /// Creates a new safe-delete operation.
    /// </summary>
    public SafeDeleteOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(SafeDeleteParams @params) => Validate(@params);

    /// <summary>
    /// Validates safe-delete parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(SafeDeleteParams @params)
    {
        if (@params.AllFiles)
        {
            if (@params.StartLine.HasValue ||
                @params.StartColumn.HasValue ||
                @params.EndLine.HasValue ||
                @params.EndColumn.HasValue ||
                !string.IsNullOrWhiteSpace(@params.SymbolName))
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with startLine, startColumn, endLine, endColumn, or symbolName.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            {
                if (!PathResolver.IsAbsolutePath(@params.SourceFile))
                    throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

                if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
                    throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!@params.StartLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startLine is required.");

        if (!@params.StartColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startColumn is required.");

        if (!@params.EndLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endLine is required.");

        if (!@params.EndColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endColumn is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.StartLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.StartColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (@params.EndLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.EndColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (@params.EndLine.Value < @params.StartLine.Value ||
            (@params.EndLine.Value == @params.StartLine.Value && @params.EndColumn.Value < @params.StartColumn.Value))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        SafeDeleteParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = GetSelectionSpan(sourceText, @params);
        var symbol = ResolveSelectedSymbol(
            root, semanticModel, span, @params, cancellationToken);

        symbol = NormalizeDeletableSymbol(symbol);
        ValidateSymbolCanBeDeleted(symbol);

        var declarationDocuments = await GetDeclarationDocumentsAsync(symbol, cancellationToken);
        foreach (var declarationDocument in declarationDocuments)
            DocumentEditableHelpers.ValidateDocumentIsEditable(declarationDocument, Context.Workspace);

        var usages = await FindUsagesAsync(symbol, cancellationToken);
        if (!CanSafelyDelete(usages))
            throw CreateHasUsagesException(symbol, usages);

        var plan = await BuildPlanAsync(symbol, cancellationToken);
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
                Name = symbol.Name,
                FullyQualifiedName = symbol.ToDisplayString(),
                Kind = SymbolKindMapper.Map(symbol)
            },
            0,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>ExtractMethodOperation.ExecuteAllFilesAsync</c>
    /// / <c>MakeStaticOperation.ExecuteAllFilesAsync</c> /
    /// <c>InlineConstantOperation.ExecuteAllFilesAsync</c>) and deletes
    /// every eligible unused <c>private</c> (or local) declaration under
    /// today's single-site rules. Public / protected / internal symbols are
    /// skipped in bulk (single-site can still delete them) so solution-local
    /// allFiles cannot wipe public API that simply has no in-solution callers.
    /// Optional <c>sourceFile</c> limits the walk to that one file. Symbols
    /// with remaining usages, uneditable / source-generated docs, linked
    /// multi-views that diverge, and otherwise ineligible declarations are
    /// skipped rather than failing the walk. Declarations are considered in
    /// descending <c>SpanStart</c> order so nested / later members are
    /// deleted before outer types; each successful delete re-resolves the
    /// document (and the walk repeats while progress is made) so cascading
    /// unused symbols unlock. When every declaration is a no-op, succeeds
    /// with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        SafeDeleteParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);
        var deletedCountByDoc = new Dictionary<DocumentId, int>();

        // Revisit after later deletions unlock newly-unused symbols (e.g. a
        // helper only referenced by another unused member deleted earlier).
        bool madeProgress;
        do
        {
            madeProgress = false;
            foreach (var linkedDocuments in documentGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Linked multi-project views of the same path can diverge under
                // preprocessor symbols / references; skip rather than coalescing
                // a delete that only one compilation can honor (same contract as
                // pull_members_up / push_members_down / extract_base_class allFiles).
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
                    foreach (var declaration in CollectDeletableDeclarations(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            updated = await TryDeleteOneAsync(
                                currentDocument,
                                declaration,
                                semanticModel,
                                currentSolution,
                                cancellationToken);
                        }
                        catch (RefactoringException)
                        {
                            updated = null;
                        }

                        if (updated != null)
                            break;
                    }

                    if (updated == null)
                        break;

                    var beforeSolution = currentSolution;
                    currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                        beforeSolution,
                        updated,
                        Context.Workspace,
                        cancellationToken);

                    deletedCountByDoc[primary.Id] =
                        deletedCountByDoc.GetValueOrDefault(primary.Id) + 1;
                    madeProgress = true;
                }
            }
        }
        while (madeProgress);

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
                var deletedCount = deletedCountByDoc.GetValueOrDefault(document.Id);
                if (deletedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        deletedCount = Math.Max(deletedCount, deletedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = deletedCount > 0
                        ? BuildAllFilesDescription(deletedCount)
                        : "Update after safe-deleting unused symbols",
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
    /// Preview description for a file that safe-deleted
    /// <paramref name="deletedCount"/> unused symbols.
    /// </summary>
    internal static string BuildAllFilesDescription(int deletedCount) =>
        deletedCount == 1
            ? "Safe-delete unused symbol"
            : $"Safe-delete {deletedCount} unused symbols";

    /// <summary>
    /// Collects every declaration node that single-site safe-delete can
    /// remove (methods, properties, events, fields, locals, types, enums,
    /// delegates, enum members, local functions, constructors, destructors,
    /// operators, indexers) in descending <c>SpanStart</c> then span-length
    /// order so nested / later members are considered before outer types.
    /// <see cref="TryDeleteOneAsync"/> further restricts bulk deletes to
    /// private / local symbols via <see cref="IsAllFilesDeletableAccessibility"/>.
    /// </summary>
    internal static IReadOnlyList<SyntaxNode> CollectDeletableDeclarations(SyntaxNode root) =>
        root.DescendantNodes()
            .Where(IsDeletableDeclarationNode)
            .OrderByDescending(node => node.SpanStart)
            .ThenByDescending(node => node.Span.Length)
            .ToList();

    private static bool IsDeletableDeclarationNode(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax => true,
        PropertyDeclarationSyntax => true,
        IndexerDeclarationSyntax => true,
        EventDeclarationSyntax => true,
        OperatorDeclarationSyntax => true,
        ConversionOperatorDeclarationSyntax => true,
        // Static constructors are runtime-invoked with no source refs; never
        // bulk-delete them (Codex P1). Instance constructors remain eligible.
        ConstructorDeclarationSyntax constructor when
            !constructor.Modifiers.Any(SyntaxKind.StaticKeyword) => true,
        DestructorDeclarationSyntax => true,
        TypeDeclarationSyntax => true,
        EnumDeclarationSyntax => true,
        DelegateDeclarationSyntax => true,
        EnumMemberDeclarationSyntax => true,
        LocalFunctionStatementSyntax => true,
        VariableDeclaratorSyntax declarator when IsDeletableVariableDeclarator(declarator) => true,
        _ => false
    };

    /// <summary>
    /// Field / event-field declarators, and ordinary locals — but not
    /// <c>using var</c> / <c>await using var</c>, whose acquisition+disposal
    /// is the behavior even when the variable is unreferenced (Codex P1).
    /// </summary>
    private static bool IsDeletableVariableDeclarator(VariableDeclaratorSyntax declarator)
    {
        if (declarator.Parent is not VariableDeclarationSyntax variableDeclaration)
            return false;

        return variableDeclaration.Parent switch
        {
            FieldDeclarationSyntax or EventFieldDeclarationSyntax => true,
            LocalDeclarationStatementSyntax local => local.UsingKeyword == default,
            _ => false
        };
    }

    private async Task<Solution?> TryDeleteOneAsync(
        Document document,
        SyntaxNode declaration,
        SemanticModel semanticModel,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var declared = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
        if (declared == null)
            return null;

        var symbol = NormalizeDeletableSymbol(declared);
        if (!IsAllFilesDeletableAccessibility(symbol))
            return null;

        if (symbol is IMethodSymbol method &&
            await IsApplicationEntryPointCandidateAsync(document, method, cancellationToken))
        {
            return null;
        }

        try
        {
            ValidateSymbolCanBeDeleted(symbol);
        }
        catch (RefactoringException)
        {
            return null;
        }

        var declarationDocuments = await GetDeclarationDocumentsAsync(symbol, solution, cancellationToken);
        if (declarationDocuments.Any(declarationDocument =>
                !DocumentEditableHelpers.IsDocumentEditable(declarationDocument, Context.Workspace)))
        {
            return null;
        }

        var usages = await FindUsagesAsync(symbol, solution, cancellationToken);
        if (!CanSafelyDelete(usages))
            return null;

        var plan = await BuildPlanAsync(symbol, cancellationToken);
        return await ApplyPlanAsync(solution, plan, cancellationToken);
    }


    private static bool IsAllFilesDeletableAccessibility(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.NotApplicable;

    /// <summary>
    /// True for the compilation entry point, or a static <c>Main</c> with an
    /// entry-point-legal signature when the host project has no recorded entry
    /// point (class-library TempWorkspace). Same shape as
    /// <c>ChangeSignatureOperation.IsApplicationEntryPointCandidate</c> /
    /// <c>ConvertToAsyncOperation</c> (Codex P1 / CS5001).
    /// </summary>
    private static async Task<bool> IsApplicationEntryPointCandidateAsync(
        Document document,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
            return false;

        var entryPoint = compilation.GetEntryPoint(cancellationToken);
        if (entryPoint != null &&
            SymbolEqualityComparer.Default.Equals(entryPoint, method))
        {
            return true;
        }

        if (!method.IsStatic ||
            !string.Equals(method.Name, "Main", StringComparison.Ordinal) ||
            method.Parameters.Length > 1)
        {
            return false;
        }

        if (method.Parameters.Length == 1)
        {
            var parameterType = method.Parameters[0].Type;
            var isStringArray = parameterType is IArrayTypeSymbol
            {
                ElementType.SpecialType: SpecialType.System_String
            };
            var isReadOnlySpanOfString =
                parameterType is INamedTypeSymbol
                {
                    Name: "ReadOnlySpan",
                    TypeArguments: [{ SpecialType: SpecialType.System_String }]
                };
            if (!isStringArray && !isReadOnlySpanOfString)
                return false;
        }

        return method.ReturnsVoid ||
               method.ReturnType.SpecialType is SpecialType.System_Int32 ||
               method.ReturnType.Name is "Task" or "ValueTask";
    }

    internal static TextSpan GetSelectionSpan(SourceText sourceText, SafeDeleteParams @params)
    {
        var startLineNumber = @params.StartLine!.Value;
        var startColumn = @params.StartColumn!.Value;
        var endLineNumber = @params.EndLine!.Value;
        var endColumn = @params.EndColumn!.Value;

        if (startLineNumber > sourceText.Lines.Count || endLineNumber > sourceText.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Selection is outside the file.");

        var startLine = sourceText.Lines[startLineNumber - 1];
        var endLine = sourceText.Lines[endLineNumber - 1];
        if (startColumn - 1 > startLine.Span.Length || endColumn - 1 > endLine.SpanIncludingLineBreak.Length)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Selection column is outside the line.");

        var startPosition = startLine.Start + startColumn - 1;
        var endPosition = endLine.Start + endColumn - 1;
        if (endPosition < startPosition)
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    private static ISymbol ResolveSelectedSymbol(
        SyntaxNode root,
        SemanticModel semanticModel,
        TextSpan span,
        SafeDeleteParams @params,
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
                    return SymbolSelectionHelpers.ConfirmSymbolName(declaredOnToken, @params.SymbolName);

                if (token.IsKind(SyntaxKind.IdentifierToken))
                {
                    var tokenSymbol = semanticModel.GetSymbolInfo(tokenNode, cancellationToken).Symbol;
                    if (tokenSymbol != null)
                        return SymbolSelectionHelpers.ConfirmSymbolName(tokenSymbol, @params.SymbolName);
                }
            }
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var declared = semanticModel.GetDeclaredSymbol(node, cancellationToken);
        if (declared != null && IdentifierOverlaps(node, span))
            return SymbolSelectionHelpers.ConfirmSymbolName(declared, @params.SymbolName);

        throw new RefactoringException(
            ErrorCodes.SymbolNotFound,
            "No symbol found at the specified selection.");
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
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        VariableDeclaratorSyntax variable => variable.Identifier,
        TypeDeclarationSyntax type => type.Identifier,
        EnumDeclarationSyntax @enum => @enum.Identifier,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier,
        EnumMemberDeclarationSyntax member => member.Identifier,
        ParameterSyntax parameter => parameter.Identifier,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier,
        ForEachStatementSyntax forEach => forEach.Identifier,
        SingleVariableDesignationSyntax designation => designation.Identifier,
        LabeledStatementSyntax labeled => labeled.Identifier,
        _ => null
    };

    private static ISymbol NormalizeDeletableSymbol(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;

        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated } &&
            associated.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or Microsoft.CodeAnalysis.SymbolKind.Event)
        {
            return associated.OriginalDefinition;
        }

        return symbol;
    }

    private static void ValidateSymbolCanBeDeleted(ISymbol symbol)
    {
        if (symbol.Kind is Microsoft.CodeAnalysis.SymbolKind.Namespace
            or Microsoft.CodeAnalysis.SymbolKind.NetModule
            or Microsoft.CodeAnalysis.SymbolKind.Assembly)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{symbol.Name}' cannot be safe-deleted.");
        }

        if (symbol is IParameterSymbol or ITypeParameterSymbol)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{symbol.Name}' cannot be safe-deleted without changing a signature.");
        }

        if (!symbol.Locations.Any(location => location.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Symbol '{symbol.Name}' is not in an editable document.");
        }
    }

    private async Task<IReadOnlyList<Document>> GetDeclarationDocumentsAsync(
        ISymbol symbol,
        CancellationToken cancellationToken) =>
        await GetDeclarationDocumentsAsync(symbol, Context.Solution, cancellationToken);

    private async Task<IReadOnlyList<Document>> GetDeclarationDocumentsAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var documents = new List<Document>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(syntax.SyntaxTree);
            if (document == null)
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Declaration of '{symbol.Name}' is not in an editable document.");
            }

            documents.Add(document);
        }

        if (documents.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Symbol '{symbol.Name}' is not in an editable document.");
        }

        return documents;
    }

    private async Task<IReadOnlyList<UsageLocation>> FindUsagesAsync(
        ISymbol symbol,
        CancellationToken cancellationToken) =>
        await FindUsagesAsync(symbol, Context.Solution, cancellationToken);

    private async Task<IReadOnlyList<UsageLocation>> FindUsagesAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, solution, cancellationToken);
        var usages = new List<UsageLocation>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Document == null || !location.Location.IsInSource)
                    continue;

                if (SymbolSelectionHelpers.IsDefinitionLocation(referencedSymbol.Definition, location.Location))
                    continue;

                var lineSpan = location.Location.GetLineSpan();
                usages.Add(new UsageLocation(
                    location.Document.FilePath ?? lineSpan.Path,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    GetSnippet(location.Location)));
            }
        }

        usages.AddRange(FindContractObligations(symbol));
        return usages;
    }

    private static IReadOnlyList<UsageLocation> FindContractObligations(ISymbol symbol)
    {
        var obligations = new List<UsageLocation>();

        switch (symbol)
        {
            case IMethodSymbol method:
                AddOverrideObligation(obligations, method.OverriddenMethod, method.IsOverride);
                AddExplicitImplementations(obligations, method.ExplicitInterfaceImplementations);
                AddImplicitInterfaceImplementations(obligations, method);
                break;
            case IPropertySymbol property:
                AddOverrideObligation(obligations, property.OverriddenProperty, property.IsOverride);
                AddExplicitImplementations(obligations, property.ExplicitInterfaceImplementations);
                AddImplicitInterfaceImplementations(obligations, property);
                break;
            case IEventSymbol @event:
                AddOverrideObligation(obligations, @event.OverriddenEvent, @event.IsOverride);
                AddExplicitImplementations(obligations, @event.ExplicitInterfaceImplementations);
                AddImplicitInterfaceImplementations(obligations, @event);
                break;
        }

        return obligations;
    }

    private static void AddOverrideObligation(List<UsageLocation> usages, ISymbol? overridden, bool isOverride)
    {
        if (!isOverride || overridden == null)
            return;

        usages.Add(ToContractUsage(overridden, "overrides"));
    }

    private static void AddExplicitImplementations(List<UsageLocation> usages, IEnumerable<ISymbol> implementations)
    {
        foreach (var implemented in implementations)
            usages.Add(ToContractUsage(implemented, "implements"));
    }

    private static void AddImplicitInterfaceImplementations(List<UsageLocation> usages, ISymbol symbol)
    {
        if (symbol.ContainingType == null)
            return;

        foreach (var iface in symbol.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(symbol.Name))
            {
                var implementation = symbol.ContainingType.FindImplementationForInterfaceMember(member);
                if (implementation == null)
                    continue;

                if (!SymbolEqualityComparer.Default.Equals(implementation, symbol) &&
                    !SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, symbol.OriginalDefinition))
                {
                    continue;
                }

                usages.Add(ToContractUsage(member, "implements"));
            }
        }
    }

    private static UsageLocation ToContractUsage(ISymbol contractMember, string relation)
    {
        var location = contractMember.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        var display = contractMember.ToDisplayString();
        if (location == null)
        {
            return new UsageLocation(
                contractMember.ContainingType?.ToDisplayString() ?? display,
                0,
                0,
                $"{relation} {display}");
        }

        var lineSpan = location.GetLineSpan();
        return new UsageLocation(
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            $"{relation} {display}");
    }

    private static bool CanSafelyDelete(IReadOnlyList<UsageLocation> usages) => usages.Count == 0;

    private static string? GetSnippet(Location location)
    {
        if (location.SourceTree == null)
            return null;

        var text = location.SourceTree.GetText();
        var line = location.GetLineSpan().StartLinePosition.Line;
        if (line < 0 || line >= text.Lines.Count)
            return null;

        return text.Lines[line].ToString().Trim();
    }

    private static RefactoringException CreateHasUsagesException(
        ISymbol symbol,
        IReadOnlyList<UsageLocation> usages)
    {
        var locations = usages
            .Select(usage => new Dictionary<string, object>
            {
                ["file"] = usage.File,
                ["line"] = usage.Line,
                ["column"] = usage.Column,
                ["snippet"] = usage.Snippet ?? string.Empty
            })
            .ToList();

        return new RefactoringException(
            ErrorCodes.MemberHasUsages,
            $"Cannot safely delete '{symbol.Name}' because it has {usages.Count} remaining reference(s).",
            new Dictionary<string, object>
            {
                ["symbolName"] = symbol.Name,
                ["usageCount"] = usages.Count,
                ["locations"] = locations
            },
            ["Remove or update the remaining references, then retry."]);
    }

    private static async Task<DeletePlan> BuildPlanAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        var removals = new List<NodeRemoval>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var removable = GetRemovableNode(syntax);
            removals.Add(new NodeRemoval(removable.SyntaxTree, removable.Span, removable.ToFullString().Trim()));
        }

        if (removals.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate a declaration to delete for '{symbol.Name}'.");
        }

        return new DeletePlan(symbol.Name, symbol.ToDisplayString(), SymbolKindMapper.Map(symbol), removals);
    }

    private static SyntaxNode GetRemovableNode(SyntaxNode declaration)
    {
        if (declaration is VariableDeclaratorSyntax declarator &&
            declarator.Parent is VariableDeclarationSyntax variableDeclaration &&
            variableDeclaration.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax
                or LocalDeclarationStatementSyntax)
        {
            return variableDeclaration.Variables.Count == 1
                ? variableDeclaration.Parent
                : declarator;
        }

        if (declaration is MemberDeclarationSyntax member)
            return member;

        if (declaration is LocalFunctionStatementSyntax localFunction)
            return localFunction;

        var name = GetDeclarationIdentifier(declaration)?.Text ?? declaration.Kind().ToString();
        throw new RefactoringException(
            ErrorCodes.InvalidSymbolKind,
            $"Declaration '{name}' is not a form that can be safely deleted.");
    }

    private static async Task<Solution> ApplyPlanAsync(
        Solution solution,
        DeletePlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var group in plan.Removals.GroupBy(removal => removal.SyntaxTree))
        {
            var document = solution.GetDocument(group.Key)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    "A declaration document is no longer in the workspace.");

            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var nodes = group
                .Select(removal => root.DescendantNodesAndSelf()
                    .FirstOrDefault(node => node.Span == removal.Span)
                    ?? GetRemovableNode(root.FindNode(removal.Span, getInnermostNodeForTie: true)))
                .Distinct()
                .ToList();

            var newRoot = root.RemoveNodes(nodes, SyntaxRemoveOptions.KeepNoTrivia)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Deleting the symbol produced an empty document root.");

            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        return solution;
    }

    private static RefactoringResult CreatePreviewResult(Guid operationId, DeletePlan plan)
    {
        var pendingChanges = plan.Removals
            .Select(removal => new PendingChange
            {
                File = removal.SyntaxTree.FilePath ?? string.Empty,
                ChangeType = ChangeKind.Modify,
                Description = $"Delete unused {plan.Kind.ToString().ToLowerInvariant()} '{plan.Name}'",
                BeforeSnippet = removal.Text,
                AfterSnippet = "// (removed)"
            })
            .ToList();

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record UsageLocation(string File, int Line, int Column, string? Snippet);

    private sealed record NodeRemoval(SyntaxTree SyntaxTree, TextSpan Span, string Text);

    private sealed record DeletePlan(
        string Name,
        string FullyQualifiedName,
        Contracts.Enums.SymbolKind Kind,
        IReadOnlyList<NodeRemoval> Removals);
}

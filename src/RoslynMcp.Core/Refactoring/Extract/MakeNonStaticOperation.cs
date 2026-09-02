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
/// Removes the <c>static</c> modifier from a selected static method when a
/// valid instance receiver exists for every call site, and rewrites type-name
/// invocations and method-group conversions to that receiver (or <c>this</c>
/// in the same type). Optional <c>allFiles</c> walks every C# document (or
/// the optional single <c>sourceFile</c>) and makes every eligible ordinary
/// static method an instance method, skipping ineligible methods rather
/// than throwing.
/// </summary>
public sealed class MakeNonStaticOperation : RefactoringOperationBase<MakeNonStaticParams>
{
    /// <summary>
    /// Creates a new make-non-static operation.
    /// </summary>
    public MakeNonStaticOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(MakeNonStaticParams @params) => Validate(@params);

    /// <summary>
    /// Validates make-non-static parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(MakeNonStaticParams @params)
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
        MakeNonStaticParams @params,
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

        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = GetSelectionSpan(sourceText, @params);
        var symbol = ResolveSelectedSymbol(root, semanticModel, span, @params, cancellationToken);
        var method = NormalizeMethodSymbol(symbol);
        ValidateMethodCanBeMadeNonStatic(method);

        var declarationDocuments = await GetDeclarationDocumentsAsync(method, cancellationToken);
        foreach (var declarationDocument in declarationDocuments)
            ValidateDocumentIsEditable(declarationDocument, Context.Workspace);

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

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>ConvertToAsyncOperation.ExecuteAllFilesAsync</c> /
    /// <c>AddNullChecksOperation.ExecuteAllFilesAsync</c> /
    /// <c>ConvertPropertyOperation.ExecuteAllFilesAsync</c> /
    /// <c>MakeStaticOperation.ExecuteAllFilesAsync</c>) and makes every
    /// eligible ordinary static method an instance method using today's
    /// rewrite (remove <c>static</c>, update type-name call sites and
    /// method-group conversions to a valid instance receiver or
    /// <c>this</c>). Optional <c>sourceFile</c> limits the walk to that
    /// one file. Already-instance, extension, virtual/override/abstract/extern,
    /// static-class, interface / interface-implementing, no-valid-receiver,
    /// uneditable, conditional-access, and otherwise ineligible methods are
    /// skipped rather than failing the walk. When two plans claim the same
    /// span, the later claim is skipped. When every method is a no-op,
    /// succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        MakeNonStaticParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = FilterDocumentsBySourceFile(allDocuments, @params.SourceFile);

        var acceptedDeclarations = new List<DeclarationEdit>();
        var acceptedCallSites = new List<CallSiteEdit>();
        var claimedSpans = new HashSet<(SyntaxTree Tree, TextSpan Span)>();
        var processedMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var convertedCountByDoc = new Dictionary<DocumentId, int>();

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDocumentEditable(document, Context.Workspace))
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || semanticModel == null)
                continue;

            foreach (var methodDecl in CollectOrdinaryMethods(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!TryGetEligibleMethod(methodDecl, semanticModel, cancellationToken, out var method) ||
                        method == null)
                    {
                        continue;
                    }

                    var canonical = CanonicalPartialMethod(method);
                    if (!processedMethods.Add(canonical))
                        continue;

                    var declarationDocuments = await GetDeclarationDocumentsAsync(canonical, cancellationToken);
                    if (declarationDocuments.Any(declarationDocument =>
                            !IsDocumentEditable(declarationDocument, Context.Workspace)))
                    {
                        continue;
                    }

                    var plan = await BuildPlanAsync(canonical, cancellationToken);
                    if (PlanConflictsWithClaimedSpans(plan, claimedSpans))
                        continue;

                    foreach (var declaration in plan.Declarations)
                        claimedSpans.Add((declaration.SyntaxTree, declaration.Span));
                    foreach (var callSite in plan.CallSites)
                        claimedSpans.Add((callSite.SyntaxTree, callSite.Span));

                    acceptedDeclarations.AddRange(plan.Declarations);
                    acceptedCallSites.AddRange(plan.CallSites);
                    convertedCountByDoc[document.Id] =
                        convertedCountByDoc.GetValueOrDefault(document.Id) + 1;
                }
                catch (RefactoringException)
                {
                    // Skip ineligible / uneditable / no-valid-receiver /
                    // conditional-access methods rather than failing the walk.
                }
            }
        }

        if (acceptedDeclarations.Count == 0 && acceptedCallSites.Count == 0)
        {
            return RefactoringResult.Succeeded(operationId,
                new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
                null, 0, 0);
        }

        var megaPlan = new StaticPlan(
            "methods",
            "methods",
            Contracts.Enums.SymbolKind.Method,
            acceptedDeclarations,
            acceptedCallSites);
        var newSolution = await ApplyPlanAsync(originalSolution, megaPlan, cancellationToken);

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;
        var documentsToCompare = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        foreach (var document in documentsToCompare)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalDocument = originalSolution.GetDocument(document.Id);
            var currentDocument = newSolution.GetDocument(document.Id);
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
                var convertedCount = convertedCountByDoc.GetValueOrDefault(document.Id);
                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = convertedCount > 0
                        ? BuildAllFilesDescription(convertedCount)
                        : "Update call sites of methods made instance",
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
            var commitResult = await CommitChangesAsync(newSolution, cancellationToken);
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
    /// Preview description for a file that made
    /// <paramref name="convertedCount"/> methods instance.
    /// </summary>
    internal static string BuildAllFilesDescription(int convertedCount) =>
        convertedCount == 1
            ? "Make method an instance method"
            : $"Make {convertedCount} methods instance methods";

    /// <summary>
    /// Collects every <see cref="MethodDeclarationSyntax"/> in
    /// <paramref name="root"/> in deterministic span order (ordinary-kind
    /// filter is applied later via <see cref="TryGetEligibleMethod"/>).
    /// </summary>
    internal static IReadOnlyList<MethodDeclarationSyntax> CollectOrdinaryMethods(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .OrderBy(method => method.SpanStart)
            .ThenBy(method => method.Span.Length)
            .ToList();

    /// <summary>
    /// Classifies a method for allFiles using the same kind / modifier /
    /// interface / static-class filter as
    /// <see cref="ValidateMethodCanBeMadeNonStatic"/> (skip, not throw).
    /// Receiver eligibility is checked separately via today's rewrite.
    /// </summary>
    internal static bool TryGetEligibleMethod(
        MethodDeclarationSyntax methodDecl,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out IMethodSymbol? method)
    {
        method = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (method == null)
            return false;

        method = method.OriginalDefinition;
        return CanMethodBeMadeNonStatic(method);
    }

    private static bool CanMethodBeMadeNonStatic(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary)
            return false;

        if (!method.IsStatic)
            return false;

        if (method.IsExtensionMethod)
            return false;

        if (method.IsAbstract || method.IsOverride || HasVirtualModifier(method))
            return false;

        if (method.IsExtern)
            return false;

        if (method.ContainingType == null)
            return false;

        if (method.ContainingType.IsStatic)
            return false;

        if (method.ContainingType.TypeKind == TypeKind.Interface)
            return false;

        if (ImplementsInterface(method))
            return false;

        return method.Locations.Any(location => location.IsInSource);
    }

    /// <summary>
    /// Prefers the implementation part of a partial method so the walk
    /// treats definition + implementation as one method.
    /// </summary>
    internal static IMethodSymbol CanonicalPartialMethod(IMethodSymbol method)
    {
        var implementation = method.PartialImplementationPart
            ?? method.PartialDefinitionPart?.PartialImplementationPart;
        return implementation ?? method.PartialDefinitionPart ?? method;
    }

    /// <summary>
    /// Both partial definition and implementation (when present), plus
    /// <paramref name="method"/> itself.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> GetPartialMethodParts(IMethodSymbol method)
    {
        var parts = new List<IMethodSymbol> { method };
        if (method.PartialDefinitionPart != null)
            parts.Add(method.PartialDefinitionPart);
        if (method.PartialImplementationPart != null)
            parts.Add(method.PartialImplementationPart);
        if (method.PartialDefinitionPart?.PartialImplementationPart != null)
            parts.Add(method.PartialDefinitionPart.PartialImplementationPart);
        if (method.PartialImplementationPart?.PartialDefinitionPart != null)
            parts.Add(method.PartialImplementationPart.PartialDefinitionPart);
        return parts.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default);
    }

    private static IEnumerable<SyntaxReference> EnumerateDeclaringSyntaxReferences(IMethodSymbol method)
    {
        foreach (var part in GetPartialMethodParts(method))
        {
            foreach (var reference in part.DeclaringSyntaxReferences)
                yield return reference;
        }
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

    private static bool PlanConflictsWithClaimedSpans(
        StaticPlan plan,
        HashSet<(SyntaxTree Tree, TextSpan Span)> claimedSpans)
    {
        foreach (var declaration in plan.Declarations)
        {
            if (claimedSpans.Contains((declaration.SyntaxTree, declaration.Span)))
                return true;
        }

        foreach (var callSite in plan.CallSites)
        {
            if (claimedSpans.Contains((callSite.SyntaxTree, callSite.Span)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Non-throwing counterpart of <see cref="ValidateDocumentIsEditable"/>
    /// for allFiles skips.
    /// </summary>
    internal static bool IsDocumentEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
            return false;

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
            return false;

        return workspace.CanApplyChange(ApplyChangesKind.ChangeDocument);
    }

    internal static TextSpan GetSelectionSpan(SourceText sourceText, MakeNonStaticParams @params)
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
        MakeNonStaticParams @params,
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

    private static void ValidateMethodCanBeMadeNonStatic(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{method.Name}' is not an ordinary method that can be made an instance method.");
        }

        if (!method.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.AlreadyInstance,
                $"Method '{method.Name}' is already an instance method.");
        }

        if (method.IsExtensionMethod)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' is an extension method and cannot be made an instance method.");
        }

        if (method.IsAbstract || method.IsOverride || HasVirtualModifier(method))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' cannot be made an instance method because it is virtual, override, or abstract.");
        }

        if (method.IsExtern)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' cannot be made an instance method because it is extern.");
        }

        if (method.ContainingType == null)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' has no containing type.");
        }

        if (method.ContainingType.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' is in a static class and cannot be made an instance method.");
        }

        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' is an interface member and cannot be made an instance method.");
        }

        if (ImplementsInterface(method))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Method '{method.Name}' implements an interface and cannot be made an instance method.");
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
        foreach (var reference in EnumerateDeclaringSyntaxReferences(method))
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

    private async Task<StaticPlan> BuildPlanAsync(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var declarations = new List<DeclarationEdit>();
        var seenDeclarations = new HashSet<(SyntaxTree Tree, TextSpan Span)>();
        foreach (var reference in EnumerateDeclaringSyntaxReferences(method))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            if (syntax is not MethodDeclarationSyntax declaration)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidSymbolKind,
                    $"Declaration '{method.Name}' is not a method that can be made an instance method.");
            }

            if (!seenDeclarations.Add((declaration.SyntaxTree, declaration.Span)))
                continue;

            declarations.Add(new DeclarationEdit(
                declaration.SyntaxTree,
                declaration.Span,
                GetSignatureSnippet(declaration),
                GetSignatureSnippet(RemoveStaticModifier(declaration))));
        }

        if (declarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate a declaration to make non-static for '{method.Name}'.");
        }

        var callSites = await FindCallSiteEditsAsync(CanonicalPartialMethod(method), cancellationToken);
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

                if (IsInNameof(nameNode))
                    continue;

                if (IsConditionalAccessCallSite(nameNode) && NeedsConditionalAccessRewrite(nameNode, model))
                    throw CreateConditionalAccessException(method);

                var rewrite = ResolveCallSiteRewrite(nameNode, method, model, cancellationToken);
                if (rewrite == null)
                    continue;

                var replacement = CreateInstanceMemberAccess(rewrite.Receiver, nameNode)
                    .WithTriviaFrom(rewrite.Target);

                edits.Add(new CallSiteEdit(
                    location.Document.FilePath ?? string.Empty,
                    rewrite.Target.SyntaxTree,
                    rewrite.Target.Span,
                    rewrite.Target.ToString(),
                    replacement.ToString(),
                    rewrite.Receiver.ToString()));
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

    private static bool IsConditionalAccessCallSite(SimpleNameSyntax name) =>
        name.Parent is MemberBindingExpressionSyntax ||
        name.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any(conditional =>
            GetConditionalBindingName(conditional) == name);

    private static bool NeedsConditionalAccessRewrite(SimpleNameSyntax name, SemanticModel model)
    {
        if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
            return IsTypeReceiver(memberAccess.Expression, model);

        return name.Parent is not MemberBindingExpressionSyntax;
    }

    private static SimpleNameSyntax? GetConditionalBindingName(ConditionalAccessExpressionSyntax conditional) =>
        conditional.WhenNotNull switch
        {
            MemberBindingExpressionSyntax binding => binding.Name,
            InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax binding =>
                binding.Name,
            _ => null
        };

    private static RefactoringException CreateConditionalAccessException(IMethodSymbol method)
    {
        return new RefactoringException(
            ErrorCodes.InvalidSelection,
            $"Cannot make '{method.Name}' an instance method because a call site uses null-conditional access (?.) that cannot be rewritten safely.",
            new Dictionary<string, object>
            {
                ["symbolName"] = method.Name
            },
            ["Update or remove null-conditional call sites (invocations and method groups), then retry."]);
    }

    private static CallSiteRewrite? ResolveCallSiteRewrite(
        SimpleNameSyntax name,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var requiredType = GetRequiredReceiverType(name, method, model, cancellationToken);
        if (requiredType == null)
            throw CreateNoValidInstanceReceiverException(method, name);

        if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
        {
            if (!IsTypeReceiver(memberAccess.Expression, model))
                return null;

            var receiver = FindInstanceReceiver(name, method, requiredType, model, cancellationToken)
                ?? throw CreateNoValidInstanceReceiverException(method, name);
            return new CallSiteRewrite(memberAccess, receiver);
        }

        if (CanUseImplicitThis(name, method, requiredType, model, cancellationToken))
            return null;

        var prefixed = FindInstanceReceiver(name, method, requiredType, model, cancellationToken)
            ?? throw CreateNoValidInstanceReceiverException(method, name);
        return new CallSiteRewrite(name, prefixed);
    }

    private static bool IsTypeReceiver(ExpressionSyntax expression, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(expression).Symbol;
        return symbol is INamespaceOrTypeSymbol;
    }

    private static INamedTypeSymbol? GetRequiredReceiverType(
        SyntaxNode nameNode,
        IMethodSymbol selectedMethod,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(nameNode, cancellationToken);
        var bound = info.Symbol as IMethodSymbol
            ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        return bound?.ContainingType ?? selectedMethod.ContainingType;
    }

    internal static bool CanUseImplicitThis(
        SyntaxNode callSite,
        IMethodSymbol method,
        INamedTypeSymbol requiredType,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (!CanUseThis(callSite, requiredType, model, cancellationToken))
            return false;

        var enclosing = GetEnclosingMember(model, callSite, cancellationToken);
        var enclosingType = enclosing?.ContainingType;
        return enclosingType != null && !WouldRebindToHidingMember(enclosingType, method);
    }

    // Receiver eligibility is decided per call site; this helper never mutates syntax.
    private static bool CanUseThis(
        SyntaxNode callSite,
        INamedTypeSymbol requiredType,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (IsInConstructorInitializer(callSite))
            return false;

        var enclosing = GetEnclosingMember(model, callSite, cancellationToken);
        if (enclosing == null || !IsInstanceThisContext(enclosing))
            return false;

        var enclosingType = enclosing.ContainingType;
        return enclosingType != null && IsCompatibleReceiverType(enclosingType, requiredType);
    }

    private static bool IsInConstructorInitializer(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<ConstructorInitializerSyntax>().Any();

    private static bool IsInstanceThisContext(ISymbol enclosing)
    {
        for (ISymbol? current = enclosing;
             current is not null and not INamedTypeSymbol;
             current = current.ContainingSymbol)
        {
            if (current.IsStatic)
                return false;
        }

        return true;
    }

    private static ISymbol? GetEnclosingMember(
        SemanticModel model,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax localFunction:
                    return model.GetDeclaredSymbol(localFunction, cancellationToken);
                case AnonymousFunctionExpressionSyntax anonymous:
                    return model.GetSymbolInfo(anonymous, cancellationToken).Symbol;
                case AccessorDeclarationSyntax accessor:
                    return model.GetDeclaredSymbol(accessor, cancellationToken);
                case BaseMethodDeclarationSyntax method:
                    return model.GetDeclaredSymbol(method, cancellationToken);
                case ArrowExpressionClauseSyntax { Parent: BasePropertyDeclarationSyntax property }:
                    return model.GetDeclaredSymbol(property, cancellationToken);
            }
        }

        return null;
    }

    private static ExpressionSyntax? FindInstanceReceiver(
        SyntaxNode callSite,
        IMethodSymbol method,
        INamedTypeSymbol requiredType,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (CanUseThis(callSite, requiredType, model, cancellationToken))
        {
            var enclosingType = GetEnclosingMember(model, callSite, cancellationToken)?.ContainingType;
            if (enclosingType != null)
            {
                var thisReceiver = QualifyReceiverIfNeeded(
                    SyntaxFactory.ThisExpression(),
                    enclosingType,
                    method,
                    requiredType,
                    model,
                    callSite.SpanStart);
                if (thisReceiver != null)
                    return thisReceiver;
            }
        }

        var candidates = new List<ISymbol>();
        foreach (var symbol in model.LookupSymbols(callSite.SpanStart))
        {
            if (!IsUsableInstanceCandidate(symbol, requiredType, model, callSite, cancellationToken))
                continue;

            candidates.Add(symbol);
        }

        var unique = candidates
            .Distinct<ISymbol>(SymbolEqualityComparer.Default)
            .ToList();
        if (unique.Count != 1 || string.IsNullOrEmpty(unique[0].Name))
            return null;

        var receiverType = GetDeclaredType(unique[0]);
        if (receiverType == null)
            return null;

        return QualifyReceiverIfNeeded(
            CreateReceiverIdentifier(unique[0].Name),
            receiverType,
            method,
            requiredType,
            model,
            callSite.SpanStart);
    }

    private static bool IsUsableInstanceCandidate(
        ISymbol symbol,
        INamedTypeSymbol requiredType,
        SemanticModel model,
        SyntaxNode callSite,
        CancellationToken cancellationToken)
    {
        if (symbol is IParameterSymbol { IsThis: true })
            return false;

        ITypeSymbol? type = GetDeclaredType(symbol);
        if (type == null || !IsCompatibleReceiverType(type, requiredType))
            return false;

        if (symbol is ILocalSymbol local
            && !IsDefinitelyAssignedAt(local, callSite, model))
        {
            return false;
        }

        if (symbol.IsStatic || symbol is ILocalSymbol or IParameterSymbol)
            return true;

        var enclosing = GetEnclosingMember(model, callSite, cancellationToken);
        return enclosing != null && IsInstanceThisContext(enclosing);
    }

    private static ITypeSymbol? GetDeclaredType(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IFieldSymbol field => field.Type,
        IPropertySymbol { GetMethod: not null } property => property.Type,
        _ => null
    };

    private static bool IsDefinitelyAssignedAt(
        ILocalSymbol local,
        SyntaxNode callSite,
        SemanticModel model)
    {
        var statement = callSite.FirstAncestorOrSelf<StatementSyntax>();
        var expression = callSite.FirstAncestorOrSelf<ExpressionSyntax>();

        DataFlowAnalysis? analysis;
        try
        {
            analysis = statement != null
                ? model.AnalyzeDataFlow(statement)
                : expression != null
                    ? model.AnalyzeDataFlow(expression)
                    : null;
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (analysis is null || !analysis.Succeeded)
            return false;

        foreach (var assigned in analysis.DefinitelyAssignedOnEntry)
        {
            if (SymbolEqualityComparer.Default.Equals(assigned, local))
                return true;
        }

        return false;
    }

    internal static bool IsCompatibleReceiverType(ITypeSymbol type, INamedTypeSymbol target)
    {
        for (ITypeSymbol? current = type; current != null; current = current.BaseType)
        {
            if (ConstructedTypesMatch(current, target))
                return true;
        }

        return false;
    }

    private static bool ConstructedTypesMatch(ITypeSymbol candidate, INamedTypeSymbol required)
    {
        if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, required.OriginalDefinition))
            return false;

        if (candidate is not INamedTypeSymbol namedCandidate)
            return false;

        if (namedCandidate.TypeArguments.Length != required.TypeArguments.Length)
            return false;

        for (var i = 0; i < required.TypeArguments.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(namedCandidate.TypeArguments[i], required.TypeArguments[i]))
                return false;
        }

        return true;
    }

    private static bool WouldRebindToHidingMember(INamedTypeSymbol receiverType, IMethodSymbol method)
    {
        for (INamedTypeSymbol? type = receiverType; type != null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(
                    type.OriginalDefinition,
                    method.ContainingType.OriginalDefinition))
            {
                return false;
            }

            if (DeclaresHidingInstanceMethod(type, method))
                return true;
        }

        return false;
    }

    private static bool DeclaresHidingInstanceMethod(INamedTypeSymbol type, IMethodSymbol method)
    {
        foreach (var member in type.GetMembers(method.Name))
        {
            if (member is not IMethodSymbol candidate
                || candidate.IsStatic
                || candidate.IsOverride
                || candidate.Parameters.Length != method.Parameters.Length
                || candidate.TypeParameters.Length != method.TypeParameters.Length)
            {
                continue;
            }

            var parametersMatch = true;
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(
                        candidate.Parameters[i].Type.OriginalDefinition,
                        method.Parameters[i].Type.OriginalDefinition))
                {
                    parametersMatch = false;
                    break;
                }
            }

            if (parametersMatch)
                return true;
        }

        return false;
    }

    private static ExpressionSyntax? QualifyReceiverIfNeeded(
        ExpressionSyntax receiver,
        ITypeSymbol receiverType,
        IMethodSymbol method,
        INamedTypeSymbol requiredType,
        SemanticModel model,
        int position)
    {
        var namedReceiver = receiverType as INamedTypeSymbol ?? receiverType.BaseType;
        if (namedReceiver == null || !WouldRebindToHidingMember(namedReceiver, method))
            return receiver;

        var typeName = requiredType.ToMinimalDisplayString(model, position);
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var typeSyntax = SyntaxFactory.ParseTypeName(typeName);
        if (typeSyntax.ContainsDiagnostics)
            return null;

        return SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(typeSyntax, receiver));
    }

    private static IdentifierNameSyntax CreateReceiverIdentifier(string name)
    {
        var keywordKind = SyntaxFacts.GetKeywordKind(name);
        if (keywordKind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(keywordKind))
        {
            return SyntaxFactory.IdentifierName(
                SyntaxFactory.VerbatimIdentifier(
                    SyntaxFactory.TriviaList(),
                    name,
                    name,
                    SyntaxFactory.TriviaList()));
        }

        return SyntaxFactory.IdentifierName(name);
    }

    private static RefactoringException CreateNoValidInstanceReceiverException(
        IMethodSymbol method,
        SyntaxNode callSite)
    {
        return new RefactoringException(
            ErrorCodes.NoValidInstanceReceiver,
            $"Cannot make '{method.Name}' an instance method because a call site has no valid instance receiver.",
            new Dictionary<string, object>
            {
                ["symbolName"] = method.Name,
                ["callSite"] = callSite.ToString()
            },
            ["Introduce a unique instance of the containing type at each call site, or keep the method static."]);
    }

    private static ExpressionSyntax CreateInstanceMemberAccess(ExpressionSyntax receiver, SimpleNameSyntax name)
    {
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
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
                .ToDictionary(callSite => callSite.Span, callSite => callSite);
            var declarationSpans = plan.Declarations
                .Where(declaration => declaration.SyntaxTree == tree)
                .Select(declaration => declaration.Span)
                .ToHashSet();

            var rewriteTargets = root.DescendantNodes()
                .Where(node => callSiteSpans.ContainsKey(node.Span) &&
                    (node is MemberAccessExpressionSyntax || node is SimpleNameSyntax))
                .ToList();
            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => declarationSpans.Contains(method.Span))
                .ToList();

            var replacements = rewriteTargets.ToDictionary(
                target => target.Span,
                target => RewriteCallSite(target, callSiteSpans[target.Span]));

            var methodAnn = new SyntaxAnnotation("make-non-static-method");
            var rewriteAnn = new SyntaxAnnotation("make-non-static-call");

            // Annotate methods and call sites in separate ReplaceNodes
            // passes. A later method's declaration can contain an earlier
            // method's call site; Roslyn ignores descendant replacements
            // when the ancestor is in the same list.
            if (methods.Count > 0)
            {
                root = root.ReplaceNodes(methods, (original, _) =>
                    original.WithAdditionalAnnotations(methodAnn));
            }

            if (rewriteTargets.Count > 0)
            {
                var rewriteSpans = rewriteTargets.Select(target => target.Span).ToHashSet();
                var currentRewrites = root.DescendantNodes()
                    .Where(node => rewriteSpans.Contains(node.Span) &&
                        (node is MemberAccessExpressionSyntax || node is SimpleNameSyntax))
                    .ToList();
                if (currentRewrites.Count > 0)
                {
                    root = root.ReplaceNodes(currentRewrites, (original, _) =>
                        original.WithAdditionalAnnotations(rewriteAnn));
                }
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
                root = root.ReplaceNodes(annotatedMethods, (original, _) => RemoveStaticModifier(original));

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    private static SyntaxNode RewriteCallSite(SyntaxNode original, CallSiteEdit edit)
    {
        var receiver = SyntaxFactory.ParseExpression(edit.Receiver);
        SimpleNameSyntax? name = original switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            SimpleNameSyntax simpleName => simpleName,
            _ => null
        };

        if (name == null)
            return original;

        return CreateInstanceMemberAccess(receiver, name).WithTriviaFrom(original);
    }

    internal static MethodDeclarationSyntax RemoveStaticModifier(MethodDeclarationSyntax method)
    {
        var staticModifier = method.Modifiers.FirstOrDefault(token => token.IsKind(SyntaxKind.StaticKeyword));
        if (staticModifier == default)
            return method;

        var modifiers = method.Modifiers;
        var index = modifiers.IndexOf(staticModifier);

        if (modifiers.Count == 1)
        {
            var returnType = method.ReturnType.WithLeadingTrivia(staticModifier.LeadingTrivia);
            return method.WithModifiers(default).WithReturnType(returnType);
        }

        if (index == 0)
        {
            var next = modifiers[1].WithLeadingTrivia(staticModifier.LeadingTrivia);
            modifiers = modifiers.RemoveAt(0);
            modifiers = modifiers.Replace(modifiers[0], next);
            return method.WithModifiers(modifiers);
        }

        return method.WithModifiers(modifiers.RemoveAt(index));
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
                Description = $"Remove static modifier from '{plan.Name}'",
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
                Description = $"Update call site of '{plan.Name}' to use an instance receiver",
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
        string After,
        string Receiver);

    private sealed record CallSiteRewrite(SyntaxNode Target, ExpressionSyntax Receiver);

    private sealed record StaticPlan(
        string Name,
        string FullyQualifiedName,
        Contracts.Enums.SymbolKind Kind,
        IReadOnlyList<DeclarationEdit> Declarations,
        IReadOnlyList<CallSiteEdit> CallSites);
}

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

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Changes a method's return type and updates return statements, overrides,
/// and interface implementations when the conversion is safe.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and changes the return type of every eligible method
/// whose current return type can safely become <c>newReturnType</c>, skipping
/// ineligible methods rather than throwing.
/// </summary>
public sealed class ChangeReturnTypeOperation : RefactoringOperationBase<ChangeReturnTypeParams>
{
    /// <summary>
    /// Creates a new change return type operation.
    /// </summary>
    public ChangeReturnTypeOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ChangeReturnTypeParams @params) => Validate(@params);

    /// <summary>
    /// Validates change-return-type inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(ChangeReturnTypeParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.NewReturnType))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newReturnType is required.");

        if (!IsValidReturnType(@params.NewReturnType))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidReturnType,
                $"'{@params.NewReturnType}' is not a valid C# return type.");
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

            // Optional sourceFile still must be an absolute .cs path when set
            // (ChangeSignature allFiles / Copilot).
            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
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
        ChangeReturnTypeParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var methodName = @params.MethodName!;

        var document = GetDocumentOrThrow(sourceFile);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var methodDecl = FindMethodHelpers.FindMethodDeclaration(root, methodName, @params.Line, @params.Column);
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        var newReturnType = ResolveReturnType(semanticModel, methodDecl, @params.NewReturnType)
            ?? throw new RefactoringException(
                ErrorCodes.InvalidReturnType,
                $"'{@params.NewReturnType}' is not a valid C# return type.");

        if (TypeEquivalenceHelpers.TypesEquivalent(methodSymbol.ReturnType, newReturnType))
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                $"New return type '{@params.NewReturnType}' is the same as the current return type.");
        }

        ValidateNotAsyncOrTaskLike(methodSymbol, newReturnType);

        var solution = document.Project.Solution;

        var contractMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            updateOverrides: true,
            updateImplementations: true,
            solution,
            cancellationToken);
        ValidateUneditableContracts(contractMethods, newReturnType, semanticModel.Compilation);

        var relatedMethods = (await GetRelatedMethodsAsync(
                methodSymbol,
                @params.UpdateOverrides,
                @params.UpdateImplementations,
                solution,
                cancellationToken))
            .Where(SignatureOverrideHelpers.HasSourceDeclaration)
            .ToList();

        foreach (var related in relatedMethods)
        {
            ValidateNotAsyncOrTaskLike(related, newReturnType);
            ValidateNoOverloadCollision(related, newReturnType);
        }

        var declarationTargets = await CollectDeclarationTargetsAsync(
            relatedMethods,
            newReturnType,
            @params.ConvertReturnStatements,
            solution,
            cancellationToken);

        foreach (var target in declarationTargets)
            DocumentEditableHelpers.ValidateDocumentIsEditable(target.Document, Context.Workspace);

        await ValidateReferencesAsync(relatedMethods, newReturnType, solution, cancellationToken);

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
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
                Name = methodName,
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            declarationTargets.Sum(t => t.ReturnSpans.Count),
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>ChangeSignatureOperation.ExecuteAllFilesAsync</c>)
    /// and changes return types to <paramref name="params"/>.NewReturnType for
    /// every eligible <see cref="MethodDeclarationSyntax"/>. Optional
    /// <c>sourceFile</c> limits via <see cref="DocumentSourceFileFilter"/>.
    /// Linked documents that share a physical path are rewritten once and the
    /// same text is applied to every sibling <see cref="DocumentId"/> via
    /// <see cref="AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync"/>.
    /// Methods that fail existing single-site safety checks (SameLocation,
    /// async/Task-like, incompatible returns, uneditable contracts, overload
    /// collisions, iterators, unsupported call sites), uneditable /
    /// source-generated docs, and otherwise inapplicable methods are skipped
    /// rather than failing the walk. Deterministic <c>SpanStart</c> order
    /// within a file. When every file is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ChangeReturnTypeParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

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
                            semanticModel,
                            methodDecl,
                            @params,
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
                currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                    beforeSolution,
                    updated,
                    Context.Workspace,
                    cancellationToken);

                changedCountByDoc[primary.Id] =
                    changedCountByDoc.GetValueOrDefault(primary.Id) + 1;
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
                        : "Update related return type declarations",
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
    /// <paramref name="changedCount"/> method return types.
    /// </summary>
    internal static string BuildAllFilesDescription(int changedCount) =>
        changedCount == 1
            ? "Change return type"
            : $"Change {changedCount} return types";

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

    private async Task<Solution?> TryChangeOneAsync(
        Document document,
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDecl,
        ChangeReturnTypeParams @params,
        CancellationToken cancellationToken)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        var newReturnType = ResolveReturnType(semanticModel, methodDecl, @params.NewReturnType);
        if (newReturnType == null)
            return null;

        if (TypeEquivalenceHelpers.TypesEquivalent(methodSymbol.ReturnType, newReturnType))
            return null;

        ValidateNotAsyncOrTaskLike(methodSymbol, newReturnType);

        // Use the document's solution (allFiles currentSolution), not Context.Solution,
        // so DeclaringSyntaxReferences resolve after prior bulk rewrites (ChangeSignature peer).
        var solution = document.Project.Solution;

        var contractMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            updateOverrides: true,
            updateImplementations: true,
            solution,
            cancellationToken);
        ValidateUneditableContracts(contractMethods, newReturnType, semanticModel.Compilation);

        var relatedMethods = (await GetRelatedMethodsAsync(
                methodSymbol,
                @params.UpdateOverrides,
                @params.UpdateImplementations,
                solution,
                cancellationToken))
            .Where(SignatureOverrideHelpers.HasSourceDeclaration)
            .ToList();

        foreach (var related in relatedMethods)
        {
            ValidateNotAsyncOrTaskLike(related, newReturnType);
            ValidateNoOverloadCollision(related, newReturnType);
        }

        var declarationTargets = await CollectDeclarationTargetsAsync(
            relatedMethods,
            newReturnType,
            @params.ConvertReturnStatements,
            solution,
            cancellationToken);

        foreach (var target in declarationTargets)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(target.Document, Context.Workspace))
                return null;
        }

        await ValidateReferencesAsync(relatedMethods, newReturnType, solution, cancellationToken);

        var beforeText = await document.GetTextAsync(cancellationToken);
        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            cancellationToken);

        var afterDocument = newSolution.GetDocument(document.Id);
        if (afterDocument == null)
            return null;

        var afterText = await afterDocument.GetTextAsync(cancellationToken);
        if (beforeText.ContentEquals(afterText))
        {
            // Declaring file unchanged but related declarations may have
            // changed — keep the solution if any document differs.
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

    internal static bool IsValidReturnType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        var remainder = type.Trim();
        if (remainder == "void")
            return true;

        var parsed = SyntaxFactory.ParseTypeName(type);
        if (parsed.ContainsDiagnostics || parsed.IsMissing)
            return false;

        if (parsed is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return true;
        }

        var written = parsed.ToString();
        if (!string.Equals(written.Trim(), remainder, StringComparison.Ordinal))
        {
            // ParseTypeName is permissive; reject leftovers such as "int int".
            if (remainder.Length > written.Trim().Length)
                return false;
        }

        return parsed is not IdentifierNameSyntax identifier || !identifier.IsMissing;
    }

    internal static ITypeSymbol? ResolveReturnType(
        SemanticModel semanticModel,
        MethodDeclarationSyntax method,
        string newReturnType)
    {
        var trimmed = newReturnType.Trim();
        if (trimmed == "void")
            return semanticModel.Compilation.GetSpecialType(SpecialType.System_Void);

        var parsed = SyntaxFactory.ParseTypeName(newReturnType);
        if (parsed.ContainsDiagnostics || parsed.IsMissing)
            return null;

        if (parsed is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return semanticModel.Compilation.GetSpecialType(SpecialType.System_Void);
        }

        var speculative = semanticModel.GetSpeculativeTypeInfo(
            method.ReturnType.SpanStart,
            parsed,
            SpeculativeBindingOption.BindAsTypeOrNamespace);

        if (speculative.Type != null && speculative.Type.TypeKind != TypeKind.Error)
            return speculative.Type;

        return semanticModel.Compilation.GetTypeByMetadataName(trimmed);
    }

    internal static void ValidateNoOverloadCollision(IMethodSymbol method, ITypeSymbol newReturnType)
    {
        if (method.ContainingType == null)
            return;

        foreach (var candidate in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, method))
                continue;
            if (candidate.Parameters.Length != method.Parameters.Length)
                continue;
            if (candidate.TypeParameters.Length != method.TypeParameters.Length)
                continue;
            if (!ParameterTypesMatch(method, candidate))
                continue;
            if (!TypeEquivalenceHelpers.TypesEquivalent(candidate.ReturnType, newReturnType))
                continue;

            throw new RefactoringException(
                ErrorCodes.SignatureMatchesOverload,
                $"Changing the return type of '{method.Name}' would match an existing overload.");
        }
    }

    private static bool ParameterTypesMatch(IMethodSymbol method, IMethodSymbol other)
    {
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var left = method.Parameters[i];
            var right = other.Parameters[i];
            if (left.RefKind != right.RefKind)
                return false;
            if (!TypeEquivalenceHelpers.TypesEquivalent(left.Type, right.Type))
                return false;
        }

        return true;
    }

    private async Task<List<IMethodSymbol>> GetRelatedMethodsAsync(
        IMethodSymbol method,
        bool updateOverrides,
        bool updateImplementations,
        Solution solution,
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
                    symbol, solution, cancellationToken: cancellationToken);
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
                        candidate, solution, cancellationToken: cancellationToken);
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
                            !SignatureOverrideHelpers.ShareOverrideRoot(implMethod, candidate))
                        {
                            continue;
                        }

                        results.Add(ifaceMethod);
                        var otherImpls = await SymbolFinder.FindImplementationsAsync(
                            ifaceMethod,
                            solution,
                            cancellationToken: cancellationToken);
                        foreach (var other in otherImpls.OfType<IMethodSymbol>())
                            results.Add(other);
                    }
                }
            }
        }

        return results.ToList();
    }

    internal static void ValidateNotAsyncOrTaskLike(IMethodSymbol method, ITypeSymbol newReturnType)
    {
        if (!method.IsAsync && !IsTaskLike(method.ReturnType) && !IsTaskLike(newReturnType))
            return;

        throw new RefactoringException(
            ErrorCodes.AsyncReturnTypeUnsupported,
            "change_return_type does not support async methods or Task/ValueTask return types.");
    }

    internal static bool IsTaskLike(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var definition = named.OriginalDefinition;
        if (definition.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks")
            return false;

        return definition.Name is "Task" or "ValueTask";
    }

    internal static void ValidateUneditableContracts(
        IEnumerable<IMethodSymbol> methods,
        ITypeSymbol newReturnType,
        Compilation compilation)
    {
        foreach (var method in methods)
        {
            if (SignatureOverrideHelpers.HasSourceDeclaration(method))
                continue;

            if (IsCompatibleWithUneditableContract(method.ReturnType, newReturnType, compilation))
                continue;

            throw new RefactoringException(
                ErrorCodes.ReturnTypeIncompatible,
                $"Changing the return type of '{method.Name}' would break an uneditable override or interface contract.");
        }
    }

    internal static bool IsCompatibleWithUneditableContract(
        ITypeSymbol contractReturn,
        ITypeSymbol newReturn,
        Compilation compilation)
    {
        if (TypeEquivalenceHelpers.TypesEquivalent(contractReturn, newReturn))
            return true;

        if (!newReturn.IsReferenceType || !contractReturn.IsReferenceType)
            return false;

        var conversion = compilation.ClassifyConversion(newReturn, contractReturn);
        return conversion.Exists && conversion.IsImplicit && (conversion.IsIdentity || conversion.IsReference);
    }

    private async Task<List<DeclarationTarget>> CollectDeclarationTargetsAsync(
        IReadOnlyList<IMethodSymbol> methods,
        ITypeSymbol newReturnType,
        bool convertReturnStatements,
        Solution solution,
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
                        $"Method '{method.Name}' is an unsupported target for change_return_type.");
                }

                var document = solution.GetDocument(syntaxRef.SyntaxTree)
                    ?? throw new RefactoringException(
                        ErrorCodes.DocumentNotEditable,
                        $"Could not locate the document for method '{method.Name}'.");

                var model = await document.GetSemanticModelAsync(cancellationToken)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

                if (IsIterator(declaration))
                {
                    throw new RefactoringException(
                        ErrorCodes.ContainsYield,
                        $"Method '{method.Name}' is an iterator; yield return types cannot be converted by change_return_type.");
                }

                var plan = AnalyzeReturnConversion(
                    declaration,
                    model,
                    method.ReturnType,
                    newReturnType,
                    convertReturnStatements);

                targets.Add(new DeclarationTarget(
                    document,
                    declaration.Span,
                    plan.ReturnSpans,
                    plan.Kind,
                    plan.AddTerminalDefault,
                    plan.ConvertExpressionBodyToBlock,
                    ContextValidTypeHelpers.ToContextValidTypeName(newReturnType, model, declaration.ReturnType.SpanStart)));
            }
        }

        if (targets.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "The selected method is an unsupported target for change_return_type.");
        }

        return targets;
    }

    internal static ReturnRewritePlan AnalyzeReturnConversion(
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        ITypeSymbol currentReturnType,
        ITypeSymbol newReturnType,
        bool convertReturnStatements)
    {
        var currentIsVoid = IsVoid(currentReturnType, declaration.ReturnType);
        var newIsVoid = IsVoid(newReturnType);
        var returns = CollectReturnStatements(declaration);

        if (declaration.Body == null && declaration.ExpressionBody == null)
            return ReturnRewritePlan.DeclarationOnly;

        if (declaration.ExpressionBody != null)
        {
            return AnalyzeExpressionBody(
                declaration,
                model,
                currentIsVoid,
                newIsVoid,
                newReturnType,
                convertReturnStatements);
        }

        if (!currentIsVoid && !newIsVoid)
        {
            foreach (var ret in returns)
                EnsureReturnCompatible(ret, model, newReturnType, convertReturnStatements);

            return ReturnRewritePlan.DeclarationOnly;
        }

        if (!currentIsVoid && newIsVoid)
        {
            if (!convertReturnStatements && returns.Any(r => r.Expression != null))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvertReturn,
                    "Return statements cannot be converted to void because convertReturnStatements is false.");
            }

            return new ReturnRewritePlan(
                ReturnRewriteKind.StripReturnExpressions,
                returns.Select(r => r.Span).ToList(),
                AddTerminalDefault: false,
                ConvertExpressionBodyToBlock: false);
        }

        if (!convertReturnStatements)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertReturn,
                "Return statements cannot be converted from void because convertReturnStatements is false.");
        }

        var addTerminal = false;
        if (declaration.Body != null)
        {
            var flow = model.AnalyzeControlFlow(declaration.Body);
            addTerminal = flow is not { Succeeded: true, EndPointIsReachable: false };
        }

        return new ReturnRewritePlan(
            ReturnRewriteKind.AddDefaultReturns,
            returns.Select(r => r.Span).ToList(),
            addTerminal,
            ConvertExpressionBodyToBlock: false);
    }

    private static ReturnRewritePlan AnalyzeExpressionBody(
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        bool currentIsVoid,
        bool newIsVoid,
        ITypeSymbol newReturnType,
        bool convertReturnStatements)
    {
        var expression = declaration.ExpressionBody!.Expression;
        if (expression is ThrowExpressionSyntax)
            return ReturnRewritePlan.DeclarationOnly;

        if (!currentIsVoid && !newIsVoid)
        {
            EnsureExpressionCompatible(expression, model, newReturnType);
            return ReturnRewritePlan.DeclarationOnly;
        }

        if (!currentIsVoid && newIsVoid)
        {
            if (!convertReturnStatements)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvertReturn,
                    "Expression-bodied return cannot be converted to void because convertReturnStatements is false.");
            }

            return new ReturnRewritePlan(
                ReturnRewriteKind.StripReturnExpressions,
                Array.Empty<TextSpan>(),
                AddTerminalDefault: false,
                ConvertExpressionBodyToBlock: true);
        }

        if (!convertReturnStatements)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertReturn,
                "Expression-bodied void method cannot gain a return value because convertReturnStatements is false.");
        }

        return new ReturnRewritePlan(
            ReturnRewriteKind.AddDefaultReturns,
            Array.Empty<TextSpan>(),
            AddTerminalDefault: false,
            ConvertExpressionBodyToBlock: true);
    }

    private static void EnsureReturnCompatible(
        ReturnStatementSyntax ret,
        SemanticModel model,
        ITypeSymbol newReturnType,
        bool convertReturnStatements)
    {
        if (ret.Expression == null)
        {
            if (!convertReturnStatements)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvertReturn,
                    "A value-less return cannot be converted to the new return type.");
            }

            return;
        }

        if (ret.Expression is ThrowExpressionSyntax)
            return;

        EnsureExpressionCompatible(ret.Expression, model, newReturnType);
    }

    private static void EnsureExpressionCompatible(
        ExpressionSyntax expression,
        SemanticModel model,
        ITypeSymbol newReturnType)
    {
        var conversion = model.ClassifyConversion(expression, newReturnType);
        if (conversion.Exists && conversion.IsImplicit)
            return;

        throw new RefactoringException(
            ErrorCodes.ReturnTypeIncompatible,
            "New return type is not compatible with existing return statements.");
    }

    internal static IReadOnlyList<ReturnStatementSyntax> CollectReturnStatements(MethodDeclarationSyntax declaration)
    {
        if (declaration.Body == null)
            return Array.Empty<ReturnStatementSyntax>();

        return declaration.Body.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(r => !IsInNestedFunction(r, declaration))
            .ToList();
    }

    internal static bool IsVoid(ITypeSymbol type, TypeSyntax? syntax = null)
    {
        if (type.SpecialType == SpecialType.System_Void)
            return true;

        if (syntax is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return true;
        }

        return type.ToDisplayString() == "void";
    }

    private static bool IsInNestedFunction(SyntaxNode node, MethodDeclarationSyntax method)
    {
        for (var current = node.Parent; current != null && current != method; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return true;
        }

        return false;
    }

    private async Task ValidateReferencesAsync(
        IReadOnlyList<IMethodSymbol> methods,
        ITypeSymbol newReturnType,
        Solution solution,
        CancellationToken cancellationToken)
    {
        foreach (var method in methods)
        {
            var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
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
                    if (SignatureReferenceHelpers.IsDeclarationName(node, location.Location.SourceSpan))
                        continue;

                    var model = await document.GetSemanticModelAsync(cancellationToken)
                        ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation != null && SignatureReferenceHelpers.IsInvokedMethodName(invocation, location.Location.SourceSpan))
                    {
                        ValidateInvocationResultContext(invocation, newReturnType, model);
                        continue;
                    }

                    if (SignatureReferenceHelpers.IsNameOfArgument(node))
                        continue;

                    if (MethodGroupStillCompatible(node, newReturnType, model))
                        continue;

                    throw new RefactoringException(
                        ErrorCodes.UnsupportedCallSite,
                        $"Method '{method.Name}' is used as a method group or other unsupported reference and cannot be updated automatically.");
                }
            }
        }
    }

    internal static void ValidateInvocationResultContext(
        InvocationExpressionSyntax invocation,
        ITypeSymbol newReturnType,
        SemanticModel model)
    {
        if (IsUnusedInvocation(invocation) || IsVarInferredAssignment(invocation))
            return;

        var converted = model.GetTypeInfo(invocation).ConvertedType;
        if (converted == null || converted.TypeKind == TypeKind.Error)
            return;

        var conversion = model.Compilation.ClassifyConversion(newReturnType, converted);
        if (conversion.Exists && conversion.IsImplicit)
            return;

        throw new RefactoringException(
            ErrorCodes.ReturnTypeIncompatible,
            "New return type is not compatible with an invocation result context.");
    }

    internal static bool IsUnusedInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Parent is ExpressionStatementSyntax)
            return true;

        return invocation.Parent is AssignmentExpressionSyntax assignment &&
               assignment.Left is IdentifierNameSyntax identifier &&
               identifier.Identifier.ValueText == "_";
    }

    internal static bool IsVarInferredAssignment(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is EqualsValueClauseSyntax equals &&
               equals.Parent is VariableDeclaratorSyntax declarator &&
               declarator.Parent is VariableDeclarationSyntax declaration &&
               declaration.Type.IsVar;
    }

    internal static bool IsIterator(MethodDeclarationSyntax declaration)
    {
        SyntaxNode? body = declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody;
        if (body == null)
            return false;

        return body.DescendantNodesAndSelf()
            .OfType<YieldStatementSyntax>()
            .Any(yield => !IsInNestedFunction(yield, declaration));
    }


    internal static bool MethodGroupStillCompatible(
        SyntaxNode node,
        ITypeSymbol newReturnType,
        SemanticModel model)
    {
        foreach (var candidate in node.AncestorsAndSelf())
        {
            var converted = model.GetTypeInfo(candidate).ConvertedType;
            if (converted is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
                continue;

            if (invoke.ReturnType.SpecialType == SpecialType.System_Void)
                return true;

            var conversion = model.Compilation.ClassifyConversion(newReturnType, invoke.ReturnType);
            return conversion.Exists && conversion.IsImplicit;
        }

        return false;
    }

    private static async Task<Solution> ApplyChangesAsync(
        Document originatingDocument,
        IReadOnlyList<DeclarationTarget> declarations,
        CancellationToken cancellationToken)
    {
        var solution = originatingDocument.Project.Solution;
        var documentIds = declarations.Select(d => d.Document.Id).ToHashSet();

        foreach (var documentId in documentIds)
        {
            var document = solution.GetDocument(documentId)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var documentTargets = declarations.Where(d => d.Document.Id == documentId).ToList();
            var methodSpans = documentTargets.Select(d => d.MethodSpan).ToHashSet();
            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => methodSpans.Contains(m.Span))
                .ToList();

            if (methods.Count != documentTargets.Count)
            {
                throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    "Could not relocate symbol-resolved change_return_type declaration spans.");
            }

            var rewriter = new ChangeReturnTypeRewriter(documentTargets, methods);
            root = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite change_return_type targets.");

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    internal static TypeSyntax CreateReturnTypeSyntax(string newReturnType, TypeSyntax original)
    {
        return SyntaxFactory.ParseTypeName(newReturnType)
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());
    }

    internal static ReturnStatementSyntax StripReturnExpression(ReturnStatementSyntax statement)
    {
        if (statement.Expression == null)
            return statement;

        return statement
            .WithExpression(null)
            .WithReturnKeyword(statement.ReturnKeyword.WithTrailingTrivia());
    }

    internal static ReturnStatementSyntax AddDefaultReturnExpression(
        ReturnStatementSyntax statement,
        string newReturnType)
    {
        if (statement.Expression != null)
            return statement;

        var expression = CreateDefaultExpression(newReturnType);
        return statement
            .WithReturnKeyword(statement.ReturnKeyword.WithTrailingTrivia(SyntaxFactory.Space))
            .WithExpression(expression);
    }

    internal static ExpressionSyntax CreateDefaultExpression(string newReturnType)
    {
        return SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(newReturnType));
    }

    internal static ReturnStatementSyntax CreateDefaultReturnStatement(string newReturnType)
    {
        return SyntaxFactory.ReturnStatement(
            SyntaxFactory.Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            CreateDefaultExpression(newReturnType),
            SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    internal static MethodDeclarationSyntax ConvertExpressionBodyFromValueToVoid(MethodDeclarationSyntax method)
    {
        var returnStatement = SyntaxFactory.ReturnStatement();
        return method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(returnStatement))
            .NormalizeWhitespace();
    }

    internal static MethodDeclarationSyntax ConvertExpressionBodyFromVoidToValue(
        MethodDeclarationSyntax method,
        string newReturnType)
    {
        var expression = method.ExpressionBody!.Expression;
        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.ExpressionStatement(expression.WithoutTrivia()),
            CreateDefaultReturnStatement(newReturnType)
        };

        return method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(statements))
            .NormalizeWhitespace();
    }

    internal static MethodDeclarationSyntax AddTerminalDefaultReturn(
        MethodDeclarationSyntax method,
        string newReturnType)
    {
        if (method.Body == null)
            return method;

        var returnStatement = CreateDefaultReturnStatement(newReturnType)
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        return method.WithBody(method.Body.AddStatements(returnStatement));
    }

    private static bool NeedsTerminalDefault(MethodDeclarationSyntax method)
    {
        if (method.Body == null)
            return false;

        return !method.Body.Statements.OfType<ReturnStatementSyntax>().Any();
    }

    private static bool IsVoidSyntax(TypeSyntax type) =>
        type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        ChangeReturnTypeParams @params,
        Document originalDocument,
        Solution newSolution,
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
                    Description = $"Change return type of '{@params.MethodName}' to '{@params.NewReturnType}'",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = $"Change return type of '{@params.MethodName}' to '{@params.NewReturnType}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    internal readonly record struct ReturnRewritePlan(
        ReturnRewriteKind Kind,
        IReadOnlyList<TextSpan> ReturnSpans,
        bool AddTerminalDefault,
        bool ConvertExpressionBodyToBlock)
    {
        public static ReturnRewritePlan DeclarationOnly { get; } = new(
            ReturnRewriteKind.DeclarationOnly,
            Array.Empty<TextSpan>(),
            AddTerminalDefault: false,
            ConvertExpressionBodyToBlock: false);
    }

    internal enum ReturnRewriteKind
    {
        DeclarationOnly,
        StripReturnExpressions,
        AddDefaultReturns
    }

    private sealed record DeclarationTarget(
        Document Document,
        TextSpan MethodSpan,
        IReadOnlyList<TextSpan> ReturnSpans,
        ReturnRewriteKind Kind,
        bool AddTerminalDefault,
        bool ConvertExpressionBodyToBlock,
        string ReturnTypeText);

    private sealed class ChangeReturnTypeRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyList<DeclarationTarget> _targets;
        private readonly HashSet<MethodDeclarationSyntax> _methods;

        public ChangeReturnTypeRewriter(
            IReadOnlyList<DeclarationTarget> targets,
            IReadOnlyList<MethodDeclarationSyntax> methods)
        {
            _targets = targets;
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var original = _methods.FirstOrDefault(m => m.Span == node.Span);
            if (original == null)
                return base.VisitMethodDeclaration(node);

            var target = _targets.FirstOrDefault(t => t.MethodSpan == original.Span);
            if (target == null)
                return base.VisitMethodDeclaration(node);

            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            visited = visited.WithReturnType(CreateReturnTypeSyntax(target.ReturnTypeText, visited.ReturnType));

            var originalIsVoid = IsVoidSyntax(original.ReturnType);
            var newIsVoid = string.Equals(target.ReturnTypeText.Trim(), "void", StringComparison.Ordinal);
            var kind = target.Kind;
            if (kind == ReturnRewriteKind.DeclarationOnly && originalIsVoid && !newIsVoid)
                kind = ReturnRewriteKind.AddDefaultReturns;
            if (kind == ReturnRewriteKind.DeclarationOnly && !originalIsVoid && newIsVoid)
                kind = ReturnRewriteKind.StripReturnExpressions;

            if (kind == ReturnRewriteKind.AddDefaultReturns)
            {
                if (visited.ExpressionBody != null)
                    visited = ConvertExpressionBodyFromVoidToValue(visited, target.ReturnTypeText);
                else if (target.AddTerminalDefault || NeedsTerminalDefault(visited))
                    visited = AddTerminalDefaultReturn(visited, target.ReturnTypeText);
            }
            else if (kind == ReturnRewriteKind.StripReturnExpressions &&
                     visited.ExpressionBody != null)
            {
                visited = ConvertExpressionBodyFromValueToVoid(visited);
            }

            return visited;
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
            var target = _targets.FirstOrDefault(t => t.ReturnSpans.Any(span => span == node.Span));
            if (target == null)
                return visited;

            return target.Kind switch
            {
                ReturnRewriteKind.StripReturnExpressions => StripReturnExpression(visited),
                ReturnRewriteKind.AddDefaultReturns => AddDefaultReturnExpression(visited, target.ReturnTypeText),
                _ => visited
            };
        }
    }
}

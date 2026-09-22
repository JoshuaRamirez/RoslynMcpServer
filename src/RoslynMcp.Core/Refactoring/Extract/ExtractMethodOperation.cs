using System.Text;
using System.Text.RegularExpressions;
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

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts selected code into a new method.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and extracts every eligible contiguous ≥2-statement
/// proper-subset run inside method/accessor/local-function blocks.
/// </summary>
public sealed class ExtractMethodOperation : RefactoringOperationBase<ExtractMethodParams>
{
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "private", "internal", "protected", "public", "private protected", "protected internal"
    };

    /// <summary>
    /// Creates a new extract method operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public ExtractMethodOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractMethodParams @params) => Validate(@params);

    /// <summary>
    /// Validates <paramref name="params"/> the same way
    /// <see cref="ValidateParams"/> does (exposed for unit tests).
    /// </summary>
    internal static void Validate(ExtractMethodParams @params)
    {
        if (!ValidVisibilities.Contains(@params.Visibility))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"'{@params.Visibility}' is not a valid visibility modifier.");

        if (@params.AllFiles)
        {
            if (@params.StartLine.HasValue ||
                @params.StartColumn.HasValue ||
                @params.EndLine.HasValue ||
                @params.EndColumn.HasValue ||
                @params.MethodName is not null)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with startLine, startColumn, endLine, endColumn, or methodName.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!@params.StartLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startLine is required.");

        if (!@params.StartColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startColumn is required.");

        if (!@params.EndLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endLine is required.");

        if (!@params.EndColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endColumn is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IdentifierPattern.IsMatch(@params.MethodName!))
            throw new RefactoringException(ErrorCodes.InvalidNewName, $"'{@params.MethodName}' is not a valid method name.");

        if (SyntaxFacts.GetKeywordKind(@params.MethodName!) != SyntaxKind.None)
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"'{@params.MethodName}' is a C# reserved keyword.");

        if (@params.StartLine.Value < 1 || @params.EndLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line numbers must be >= 1.");

        if (@params.StartColumn.Value < 1 || @params.EndColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column numbers must be >= 1.");

        if (@params.StartLine.Value > @params.EndLine.Value ||
            (@params.StartLine.Value == @params.EndLine.Value &&
             @params.StartColumn.Value >= @params.EndColumn.Value))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "Selection start must be before end.");
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
        ExtractMethodParams @params,
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

        // Get the selection span
        var text = await document.GetTextAsync(cancellationToken);
        var startPosition = SymbolResolver.GetPosition(text, @params.StartLine!.Value, @params.StartColumn!.Value);
        var endPosition = SymbolResolver.GetPosition(text, @params.EndLine!.Value, @params.EndColumn!.Value);
        var selectionSpan = TextSpan.FromBounds(startPosition, endPosition);

        // Find nodes in selection
        var selectedNodes = GetSelectedNodes(root, selectionSpan);
        if (selectedNodes.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.EmptySelection,
                "No code selected for extraction.");
        }

        // Validate selection
        ValidateSelection(selectedNodes, semanticModel, cancellationToken);

        // Find containing method and type
        var containingMethod = selectedNodes[0].Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? selectedNodes[0].Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() as SyntaxNode
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "Selection must be inside a method.");

        var containingType = containingMethod.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "Selection must be inside a type.");

        // Analyze data flow
        var dataFlowAnalysis = AnalyzeDataFlow(selectedNodes, semanticModel, cancellationToken);

        // Build the extracted method
        var (extractedMethod, callExpression) = BuildExtractedMethod(
            @params,
            selectedNodes,
            dataFlowAnalysis,
            containingMethod,
            semanticModel,
            cancellationToken);

        // Create the new syntax tree
        var newRoot = CreateNewRoot(root, containingType, containingMethod, selectedNodes, extractedMethod, callExpression);

        // If preview mode, return without applying (but include before/after snippets)
        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                document.FilePath!,
                selectedNodes,
                extractedMethod,
                callExpression);
        }

        // Update document
        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = newDocument.Project.Solution;

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
                Name = @params.MethodName!,
                FullyQualifiedName = @params.MethodName!,
                Kind = Contracts.Enums.SymbolKind.Method
            },
            0,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>ExtractVariableOperation.ExecuteAllFilesAsync</c>
    /// / <c>ExtractConstantOperation.ExecuteAllFilesAsync</c>) and extracts
    /// every eligible contiguous ≥2-statement proper-subset run inside
    /// method/accessor/local-function blocks to a new method named from the
    /// statement text. Optional <c>sourceFile</c> limits via
    /// <see cref="DocumentSourceFileFilter"/>. Linked documents that share a
    /// physical path are rewritten once and the same text is applied to every
    /// sibling <see cref="DocumentId"/> via
    /// <see cref="PathResolver.GetPathComparisonKey"/>. Uneditable /
    /// source-generated docs, yield / multiple returns, name collisions,
    /// empty/invalid derived names, and otherwise ineligible targets are
    /// skipped rather than failing the walk. Deterministic <c>SpanStart</c>
    /// order within a file. When every file is a no-op, succeeds with empty
    /// changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ExtractMethodParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);
        var extractedCountByDoc = new Dictionary<DocumentId, int>();

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
                foreach (var run in CollectEligibleStatementRuns(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        updated = TryExtractOne(
                            currentDocument,
                            root,
                            semanticModel,
                            run,
                            @params,
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

                extractedCountByDoc[primary.Id] =
                    extractedCountByDoc.GetValueOrDefault(primary.Id) + 1;
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
                var extractedCount = extractedCountByDoc.GetValueOrDefault(document.Id);
                if (extractedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        extractedCount = Math.Max(extractedCount, extractedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = extractedCount > 0
                        ? BuildAllFilesDescription(extractedCount)
                        : "Update extract_method rewrites",
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
    /// Preview description for a file that extracted
    /// <paramref name="extractedCount"/> methods.
    /// </summary>
    internal static string BuildAllFilesDescription(int extractedCount) =>
        extractedCount == 1
            ? "Extract method"
            : $"Extract {extractedCount} methods";

    /// <summary>
    /// Collects every non-overlapping contiguous ≥2-statement proper-subset
    /// run inside method/accessor/local-function blocks. Leaves ≥1 statement
    /// in the containing block. Deterministic <c>SpanStart</c> order.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<StatementSyntax>> CollectEligibleStatementRuns(SyntaxNode root)
    {
        var results = new List<IReadOnlyList<StatementSyntax>>();

        foreach (var block in root.DescendantNodes().OfType<BlockSyntax>())
        {
            if (!IsMethodLikeBody(block))
                continue;

            var statements = block.Statements;
            var n = statements.Count;
            // Need ≥3 statements so a length≥2 run can leave ≥1 behind.
            if (n < 3)
                continue;

            // Greedy non-overlapping length-2 pairs from the start, never
            // consuming the final statement of the block.
            for (var i = 0; i + 1 < n - 1; i += 2)
            {
                results.Add(new StatementSyntax[] { statements[i], statements[i + 1] });
            }
        }

        return results
            .OrderBy(run => run[0].SpanStart)
            .ThenBy(run => run[^1].Span.End)
            .ToList();
    }

    private static bool IsMethodLikeBody(BlockSyntax block)
    {
        return block.Parent is MethodDeclarationSyntax
            or LocalFunctionStatementSyntax
            or AccessorDeclarationSyntax;
    }

    private Solution? TryExtractOne(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        IReadOnlyList<StatementSyntax> selectedStatements,
        ExtractMethodParams bulkParams,
        CancellationToken cancellationToken)
    {
        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        if (selectedStatements.Count == 0)
            return null;

        var selectedNodes = selectedStatements.Cast<SyntaxNode>().ToList();

        try
        {
            ValidateSelection(selectedNodes, semanticModel, cancellationToken);
        }
        catch (RefactoringException)
        {
            return null;
        }

        var containingMethod = selectedNodes[0].Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? selectedNodes[0].Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() as SyntaxNode;
        if (containingMethod == null)
            return null;

        var containingType = containingMethod.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return null;

        var methodName = DeriveMethodNameFromStatements(selectedStatements);
        if (methodName == null)
            return null;

        if (TypeHasMemberNamed(containingType, methodName))
            return null;

        var namedParams = new ExtractMethodParams
        {
            SourceFile = document.FilePath,
            MethodName = methodName,
            Visibility = bulkParams.Visibility,
            MakeStatic = bulkParams.MakeStatic,
            Preview = false
        };

        var dataFlowAnalysis = AnalyzeDataFlow(selectedNodes, semanticModel, cancellationToken);
        var (extractedMethod, callExpression) = BuildExtractedMethod(
            namedParams,
            selectedNodes,
            dataFlowAnalysis,
            containingMethod,
            semanticModel,
            cancellationToken);

        var newRoot = CreateNewRoot(root, containingType, containingMethod, selectedNodes, extractedMethod, callExpression);
        return document.WithSyntaxRoot(newRoot).Project.Solution;
    }

    /// <summary>
    /// Derives a PascalCase valid method identifier from statement text.
    /// Prefers first invoked simple name / created type name / sanitized text.
    /// Returns <see langword="null"/> when empty, invalid, or an unfixable keyword.
    /// </summary>
    internal static string? DeriveMethodNameFromStatements(IReadOnlyList<StatementSyntax> statements)
    {
        string? seed = null;
        foreach (var statement in statements)
        {
            foreach (var node in statement.DescendantNodesAndSelf())
            {
                seed = node switch
                {
                    InvocationExpressionSyntax invocation => PreferInvokedName(invocation),
                    ObjectCreationExpressionSyntax creation => PreferTypeName(creation.Type),
                    IdentifierNameSyntax id when seed == null => id.Identifier.ValueText,
                    _ => seed
                };
                if (seed != null && node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
                    break;
            }
            if (seed != null)
                break;
        }

        seed ??= SanitizeIdentifierSeed(statements[0].ToString());
        return FinalizeMethodName(seed);
    }

    private static bool TypeHasMemberNamed(TypeDeclarationSyntax type, string methodName)
    {
        var bare = methodName.StartsWith('@') ? methodName[1..] : methodName;
        return type.Members.Any(m => m switch
        {
            MethodDeclarationSyntax method => string.Equals(method.Identifier.ValueText, bare, StringComparison.Ordinal),
            _ => false
        });
    }

    private static string? PreferInvokedName(InvocationExpressionSyntax invocation) =>
        PreferInvokedNameFromExpression(invocation.Expression);

    private static string? PreferInvokedNameFromExpression(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            ParenthesizedExpressionSyntax paren => PreferInvokedNameFromExpression(paren.Expression),
            _ => null
        };

    private static string? PreferTypeName(TypeSyntax? type) =>
        type switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            NullableTypeSyntax n => PreferTypeName(n.ElementType),
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            _ => type == null ? null : SanitizeIdentifierSeed(type.ToString())
        };

    private static string? SanitizeIdentifierSeed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var builder = new StringBuilder(text.Length);
        var startNewWord = true;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (startNewWord && char.IsLetter(c))
                {
                    builder.Append(builder.Length == 0
                        ? char.ToUpperInvariant(c)
                        : char.ToUpperInvariant(c));
                    startNewWord = false;
                }
                else
                {
                    builder.Append(c);
                    startNewWord = false;
                }
            }
            else
            {
                startNewWord = true;
            }
        }

        var name = builder.ToString();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? FinalizeMethodName(string? seed)
    {
        if (string.IsNullOrEmpty(seed))
            return null;

        var name = seed;
        if (char.IsDigit(name[0]))
            name = "M" + name;

        if (char.IsLower(name[0]))
            name = char.ToUpperInvariant(name[0]) + name[1..];

        if (!IdentifierPattern.IsMatch(name.StartsWith('@') ? name[1..] : name) &&
            !SyntaxIdentifierValidation.IsValidIdentifier(name))
        {
            return null;
        }

        if (!SyntaxIdentifierValidation.IsValidIdentifier(name))
        {
            var keywordKind = SyntaxFacts.GetKeywordKind(name);
            if (keywordKind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(keywordKind))
            {
                name = "@" + name;
            }
            else
            {
                return null;
            }
        }

        return SyntaxIdentifierValidation.IsValidIdentifier(name) ? name : null;
    }

    private static List<SyntaxNode> GetSelectedNodes(SyntaxNode root, TextSpan selection)
    {
        var nodes = new List<SyntaxNode>();

        // Find the innermost node that contains the selection
        var node = root.FindNode(selection, getInnermostNodeForTie: true);

        // A multi-statement span often resolves to the containing BlockSyntax
        // (itself a StatementSyntax). Collect intersecting child statements
        // from that block rather than treating the block as the selection.
        if (node is BlockSyntax block)
        {
            foreach (var child in block.Statements)
            {
                if (child.Span.IntersectsWith(selection))
                {
                    nodes.Add(child);
                }
            }
        }
        else if (node is StatementSyntax)
        {
            var parent = node.Parent;
            if (parent != null)
            {
                foreach (var child in parent.ChildNodes())
                {
                    if (child.Span.IntersectsWith(selection) && child is StatementSyntax)
                    {
                        nodes.Add(child);
                    }
                }
            }
        }
        else if (node is ExpressionSyntax)
        {
            nodes.Add(node);
        }

        if (nodes.Count == 0 && node != null)
        {
            nodes.Add(node);
        }

        return nodes;
    }

    private static void ValidateSelection(
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            // Check for yield statements
            if (node.DescendantNodes().Any(n => n is YieldStatementSyntax))
            {
                throw new RefactoringException(
                    ErrorCodes.ContainsYield,
                    "Cannot extract code containing yield statements.");
            }

            // Check for multiple returns (simple heuristic)
            var returns = node.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
            if (returns.Count > 1)
            {
                throw new RefactoringException(
                    ErrorCodes.MultipleExitPoints,
                    "Selection has multiple return statements. Simplify before extraction.");
            }
        }
    }

    /// <summary>
    /// Contains data flow analysis results for method extraction.
    /// </summary>
    /// <remarks>
    /// This class holds information about:
    /// <list type="bullet">
    ///   <item>Variables that need to be passed as parameters (DataFlowsIn)</item>
    ///   <item>Variables that are written and used after selection (DataFlowsOut)</item>
    ///   <item>Return type if the selection produces a value</item>
    ///   <item>Whether ref/out parameters are needed</item>
    /// </list>
    /// </remarks>
    private sealed class DataFlowInfo
    {
        /// <summary>Variables that flow into the selection and must become parameters.</summary>
        public List<ISymbol> Parameters { get; } = new();

        /// <summary>The return type of the extracted method, if any.</summary>
        public ITypeSymbol? ReturnType { get; set; }

        /// <summary>The variable that should be returned, if any.</summary>
        public ISymbol? ReturnVariable { get; set; }

        /// <summary>Variables declared in selection but used after - require out params or return.</summary>
        public List<ISymbol> LocalsToHoist { get; } = new();

        /// <summary>True if any variables need ref parameters (modified in selection, used after).</summary>
        public bool RequiresRef { get; set; }

        /// <summary>True if the selection contains await expressions, requiring async method.</summary>
        public bool ContainsAwait { get; set; }

        /// <summary>Variables written inside selection that flow out (may need ref/out).</summary>
        public List<ISymbol> VariablesWritten { get; } = new();
    }

    /// <summary>
    /// Analyzes data flow for the selected nodes using Roslyn's semantic analysis.
    /// </summary>
    /// <param name="nodes">Selected syntax nodes.</param>
    /// <param name="semanticModel">Semantic model for analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Data flow analysis results.</returns>
    /// <remarks>
    /// Uses SemanticModel.AnalyzeDataFlow() for accurate analysis of:
    /// <list type="bullet">
    ///   <item>Variables flowing in (read before written in selection)</item>
    ///   <item>Variables flowing out (written in selection, read after)</item>
    ///   <item>Variables declared in selection</item>
    ///   <item>Complex scenarios like loops, conditional assignments, out params</item>
    /// </list>
    /// Falls back to manual analysis if Roslyn data flow analysis is not available.
    /// </remarks>
    private static DataFlowInfo AnalyzeDataFlow(
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var info = new DataFlowInfo();

        // Try to use Roslyn's built-in data flow analysis for accurate results
        var dataFlowResult = TryAnalyzeDataFlowWithRoslyn(nodes, semanticModel);

        if (dataFlowResult != null && dataFlowResult.Succeeded)
        {
            // Use Roslyn's accurate data flow analysis
            PopulateFromDataFlowAnalysis(info, dataFlowResult);
        }
        else
        {
            // Fall back to manual analysis for nodes that don't support data flow
            PopulateFromManualAnalysis(info, nodes, semanticModel, cancellationToken);
        }

        // Determine return type based on last node
        DetermineReturnType(info, nodes, semanticModel, cancellationToken);

        // Check for await expressions
        foreach (var node in nodes)
        {
            if (node.DescendantNodesAndSelf().Any(n => n is AwaitExpressionSyntax))
            {
                info.ContainsAwait = true;
                break;
            }
        }

        return info;
    }

    /// <summary>
    /// Attempts to use Roslyn's AnalyzeDataFlow for the given nodes.
    /// </summary>
    /// <param name="nodes">The selected nodes to analyze.</param>
    /// <param name="semanticModel">The semantic model.</param>
    /// <returns>DataFlowAnalysis result or null if analysis fails.</returns>
    private static DataFlowAnalysis? TryAnalyzeDataFlowWithRoslyn(
        List<SyntaxNode> nodes,
        SemanticModel semanticModel)
    {
        if (nodes.Count == 0) return null;

        // For single statement, analyze it directly
        if (nodes.Count == 1 && nodes[0] is StatementSyntax singleStatement)
        {
            try
            {
                return semanticModel.AnalyzeDataFlow(singleStatement);
            }
            catch
            {
                return null;
            }
        }

        // For multiple statements, try to analyze the range
        if (nodes.All(n => n is StatementSyntax))
        {
            var firstStatement = (StatementSyntax)nodes[0];
            var lastStatement = (StatementSyntax)nodes[^1];

            try
            {
                return semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
            }
            catch
            {
                return null;
            }
        }

        // For expression, try to analyze it
        if (nodes.Count == 1 && nodes[0] is ExpressionSyntax expression)
        {
            try
            {
                return semanticModel.AnalyzeDataFlow(expression);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Populates DataFlowInfo from Roslyn's DataFlowAnalysis results.
    /// </summary>
    /// <param name="info">The DataFlowInfo to populate.</param>
    /// <param name="dataFlow">Roslyn's data flow analysis result.</param>
    private static void PopulateFromDataFlowAnalysis(DataFlowInfo info, DataFlowAnalysis dataFlow)
    {
        // Variables that flow into the selection (read before written) become parameters
        foreach (var symbol in dataFlow.DataFlowsIn)
        {
            if (symbol is ILocalSymbol || symbol is IParameterSymbol)
            {
                info.Parameters.Add(symbol);
            }
        }

        // Variables that flow out (written in selection, read after) may need ref/out
        foreach (var symbol in dataFlow.DataFlowsOut)
        {
            if (symbol is ILocalSymbol || symbol is IParameterSymbol)
            {
                info.VariablesWritten.Add(symbol);

                // If variable flows in AND out, it needs ref
                if (dataFlow.DataFlowsIn.Contains(symbol))
                {
                    info.RequiresRef = true;
                }
                else
                {
                    // Variable only flows out - could be return value or out param
                    info.LocalsToHoist.Add(symbol);
                }
            }
        }
    }

    /// <summary>
    /// Fallback manual analysis when Roslyn data flow is not available.
    /// </summary>
    /// <param name="info">The DataFlowInfo to populate.</param>
    /// <param name="nodes">The selected nodes.</param>
    /// <param name="semanticModel">The semantic model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static void PopulateFromManualAnalysis(
        DataFlowInfo info,
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var referencedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var declaredSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var node in nodes)
        {
            foreach (var descendant in node.DescendantNodes())
            {
                var declared = semanticModel.GetDeclaredSymbol(descendant, cancellationToken);
                if (declared != null)
                {
                    declaredSymbols.Add(declared);
                }
            }

            foreach (var identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
                if (symbolInfo.Symbol != null)
                {
                    referencedSymbols.Add(symbolInfo.Symbol);
                }
            }
        }

        foreach (var symbol in referencedSymbols)
        {
            if (!declaredSymbols.Contains(symbol) &&
                (symbol is ILocalSymbol || symbol is IParameterSymbol))
            {
                info.Parameters.Add(symbol);
            }
        }
    }

    /// <summary>
    /// Determines the return type based on the last node in the selection.
    /// </summary>
    /// <param name="info">The DataFlowInfo to update.</param>
    /// <param name="nodes">The selected nodes.</param>
    /// <param name="semanticModel">The semantic model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static void DetermineReturnType(
        DataFlowInfo info,
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var lastNode = nodes[^1];

        if (lastNode is ReturnStatementSyntax returnStmt && returnStmt.Expression != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(returnStmt.Expression, cancellationToken);
            info.ReturnType = typeInfo.Type;
        }
        else if (lastNode is ExpressionStatementSyntax exprStmt)
        {
            var typeInfo = semanticModel.GetTypeInfo(exprStmt.Expression, cancellationToken);
            if (typeInfo.Type != null && typeInfo.Type.SpecialType != SpecialType.System_Void)
            {
                info.ReturnType = typeInfo.Type;
            }
        }
        else if (info.LocalsToHoist.Count == 1)
        {
            // Single variable that flows out can be the return value
            var returnVar = info.LocalsToHoist[0];
            info.ReturnVariable = returnVar;
            info.ReturnType = returnVar switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol param => param.Type,
                _ => null
            };
        }
    }

    private static (MethodDeclarationSyntax Method, ExpressionSyntax Call) BuildExtractedMethod(
        ExtractMethodParams @params,
        List<SyntaxNode> selectedNodes,
        DataFlowInfo dataFlow,
        SyntaxNode containingMethod,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        // Build parameters
        var parameters = new List<ParameterSyntax>();
        var arguments = new List<ArgumentSyntax>();

        foreach (var param in dataFlow.Parameters)
        {
            var type = param switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol p => p.Type,
                _ => null
            };

            if (type != null)
            {
                parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(param.Name))
                    .WithType(SyntaxFactory.ParseTypeName(type.ToDisplayString())));
                arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(param.Name)));
            }
        }

        // Determine return type - wrap in Task<T> if async
        TypeSyntax returnType;
        if (dataFlow.ContainsAwait)
        {
            if (dataFlow.ReturnType != null)
            {
                // async method with return value -> Task<T>
                returnType = SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("Task"),
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                            SyntaxFactory.ParseTypeName(dataFlow.ReturnType.ToDisplayString()))));
            }
            else
            {
                // async method returning void -> Task
                returnType = SyntaxFactory.ParseTypeName("Task");
            }
        }
        else
        {
            returnType = dataFlow.ReturnType != null
                ? SyntaxFactory.ParseTypeName(dataFlow.ReturnType.ToDisplayString())
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        }

        // Build method body.
        // Preserve inner trivia (comments within statements) but strip only the outer
        // leading/trailing whitespace that would cause formatting issues in the new method.
        var statements = selectedNodes
            .OfType<StatementSyntax>()
            .Select(s => PreserveInnerTrivia(s))
            .ToList();

        if (statements.Count == 0 && selectedNodes.Count > 0 && selectedNodes[0] is ExpressionSyntax expr)
        {
            // Single expression - make it a return statement
            statements.Add(SyntaxFactory.ReturnStatement(expr));
        }

        var body = SyntaxFactory.Block(statements);

        // Build modifiers - handle multi-word visibility modifiers
        var modifiers = new List<SyntaxToken>();
        var visibility = @params.Visibility.ToLowerInvariant();

        switch (visibility)
        {
            case "private protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            case "protected internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            case "public":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                break;
            case "private":
            default:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                break;
        }

        // Check if static is needed
        bool makeStatic = @params.MakeStatic ?? false;
        if (!makeStatic && containingMethod is MethodDeclarationSyntax methodDecl)
        {
            makeStatic = methodDecl.Modifiers.Any(SyntaxKind.StaticKeyword);
        }

        if (makeStatic)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        // Add async modifier if method contains await expressions
        if (dataFlow.ContainsAwait)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        }

        var method = SyntaxFactory.MethodDeclaration(returnType, @params.MethodName!)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(body)
            .NormalizeWhitespace();

        // Build call expression - wrap in await if method is async
        ExpressionSyntax call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(@params.MethodName!),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

        if (dataFlow.ContainsAwait)
        {
            call = SyntaxFactory.AwaitExpression(call);
        }

        return (method, call);
    }

    private static SyntaxNode CreateNewRoot(
        SyntaxNode root,
        TypeDeclarationSyntax containingType,
        SyntaxNode containingMethod,
        List<SyntaxNode> selectedNodes,
        MethodDeclarationSyntax extractedMethod,
        ExpressionSyntax callExpression)
    {
        // Create the replacement statement
        StatementSyntax callStatement;
        if (extractedMethod.ReturnType is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            callStatement = SyntaxFactory.ExpressionStatement(callExpression);
        }
        else
        {
            // If there's a return type, we might need to assign or return
            callStatement = SyntaxFactory.ExpressionStatement(callExpression);
        }

        // Replace selected nodes with call
        var firstNode = selectedNodes[0];
        var lastNode = selectedNodes[^1];

        var newRoot = root;

        // Remove all selected nodes except first, replace first with call
        if (selectedNodes.Count == 1)
        {
            if (firstNode is StatementSyntax)
            {
                newRoot = root.ReplaceNode(firstNode, callStatement
                    .WithLeadingTrivia(firstNode.GetLeadingTrivia())
                    .WithTrailingTrivia(firstNode.GetTrailingTrivia()));
            }
            else if (firstNode is ExpressionSyntax)
            {
                newRoot = root.ReplaceNode(firstNode, callExpression);
            }
        }
        else
        {
            // Multiple statements - more complex replacement
            var parent = firstNode.Parent;
            if (parent is BlockSyntax block)
            {
                var newStatements = new List<StatementSyntax>();
                bool replaced = false;

                foreach (var stmt in block.Statements)
                {
                    if (selectedNodes.Contains(stmt))
                    {
                        if (!replaced)
                        {
                            newStatements.Add(callStatement
                                .WithLeadingTrivia(stmt.GetLeadingTrivia()));
                            replaced = true;
                        }
                        // Skip other selected statements
                    }
                    else
                    {
                        newStatements.Add(stmt);
                    }
                }

                var newBlock = block.WithStatements(SyntaxFactory.List(newStatements));
                newRoot = root.ReplaceNode(block, newBlock);
            }
        }

        // Add the extracted method to the type
        var currentType = newRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == containingType.Identifier.Text);

        var newType = currentType.AddMembers(extractedMethod
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        newRoot = newRoot.ReplaceNode(currentType, newType);

        return newRoot;
    }

    /// <summary>
    /// Creates a preview result with before/after code snippets.
    /// </summary>
    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractMethodParams @params,
        string filePath,
        List<SyntaxNode> selectedNodes,
        MethodDeclarationSyntax extractedMethod,
        ExpressionSyntax callExpression)
    {
        // Build the "before" snippet from the selected nodes
        var beforeSnippet = string.Join(Environment.NewLine,
            selectedNodes.Select(n => n.ToFullString().Trim()));

        // Build the "after" snippet showing the method call and new method
        var callStatement = SyntaxFactory.ExpressionStatement(callExpression)
            .NormalizeWhitespace()
            .ToFullString();

        var afterSnippet = $"// Call site replacement:\r\n{callStatement}\r\n\r\n// New extracted method:\r\n{extractedMethod.ToFullString()}";

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = filePath,
                ChangeType = Contracts.Enums.ChangeKind.Modify,
                Description = $"Extract method '{@params.MethodName}' from lines {@params.StartLine!.Value}-{@params.EndLine!.Value}",
                StartLine = @params.StartLine!.Value,
                EndLine = @params.EndLine!.Value,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = afterSnippet
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    /// <summary>
    /// Preserves inner trivia (comments, etc.) within a statement while stripping only
    /// the outermost leading whitespace/newlines for formatting in the new method.
    /// </summary>
    /// <param name="statement">The statement to process.</param>
    /// <returns>The statement with preserved inner trivia and normalized outer formatting.</returns>
    /// <remarks>
    /// This method:
    /// <list type="bullet">
    ///   <item>Strips leading whitespace/newlines but preserves leading comments</item>
    ///   <item>Strips trailing whitespace/newlines but preserves trailing comments</item>
    ///   <item>Leaves all inner trivia (comments within the statement) intact</item>
    /// </list>
    /// </remarks>
    private static StatementSyntax PreserveInnerTrivia(StatementSyntax statement)
    {
        // Process leading trivia: preserve comments, strip pure whitespace at the start
        var leadingTrivia = statement.GetLeadingTrivia();
        var preservedLeading = new List<SyntaxTrivia>();
        var foundNonWhitespace = false;

        foreach (var trivia in leadingTrivia)
        {
            var kind = trivia.Kind();

            // Once we find a comment, preserve everything from there
            if (kind == SyntaxKind.SingleLineCommentTrivia ||
                kind == SyntaxKind.MultiLineCommentTrivia ||
                kind == SyntaxKind.SingleLineDocumentationCommentTrivia ||
                kind == SyntaxKind.MultiLineDocumentationCommentTrivia ||
                kind == SyntaxKind.RegionDirectiveTrivia ||
                kind == SyntaxKind.EndRegionDirectiveTrivia ||
                kind == SyntaxKind.PragmaWarningDirectiveTrivia)
            {
                foundNonWhitespace = true;
            }

            if (foundNonWhitespace)
            {
                preservedLeading.Add(trivia);
            }
            else if (kind != SyntaxKind.WhitespaceTrivia && kind != SyntaxKind.EndOfLineTrivia)
            {
                // Non-whitespace, non-comment: preserve it
                preservedLeading.Add(trivia);
                foundNonWhitespace = true;
            }
        }

        // Process trailing trivia: preserve comments, strip pure whitespace at the end
        var trailingTrivia = statement.GetTrailingTrivia();
        var preservedTrailing = new List<SyntaxTrivia>();

        foreach (var trivia in trailingTrivia)
        {
            var kind = trivia.Kind();

            if (kind == SyntaxKind.SingleLineCommentTrivia ||
                kind == SyntaxKind.MultiLineCommentTrivia)
            {
                preservedTrailing.Add(trivia);
            }
            else if (kind == SyntaxKind.EndOfLineTrivia && preservedTrailing.Count > 0)
            {
                // Keep newline after trailing comment
                preservedTrailing.Add(trivia);
            }
        }

        return statement
            .WithLeadingTrivia(preservedLeading)
            .WithTrailingTrivia(preservedTrailing);
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Organize.Utilities;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Organize;

/// <summary>
/// Adds missing using directives required to resolve unbound type references.
/// </summary>
public sealed class AddMissingUsingsOperation : RefactoringOperationBase<AddMissingUsingsParams>
{
    /// <summary>
    /// Creates a new add missing usings operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public AddMissingUsingsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AddMissingUsingsParams @params)
    {
        if (@params.AllFiles)
        {
            // When processing all files, sourceFile is optional; validate path
            // shape only (filter throws SourceFileNotFound / SourceNotInWorkspace).
            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required when allFiles is false.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        AddMissingUsingsParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
        {
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);
        }

        return await ExecuteSingleFileAsync(operationId, @params.SourceFile!, @params.Preview, cancellationToken);
    }

    /// <summary>
    /// Processes a single file to add missing using directives.
    /// </summary>
    private async Task<RefactoringResult> ExecuteSingleFileAsync(
        Guid operationId,
        string sourceFile,
        bool preview,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(sourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find unresolved type names using defined diagnostic IDs
        var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == DiagnosticIds.TypeOrNamespaceNotFound ||
                        d.Id == DiagnosticIds.NameDoesNotExist ||
                        d.Id == DiagnosticIds.TypeOrNamespaceDoesNotExistInNamespace)
            .ToList();

        if (diagnostics.Count == 0)
        {
            // No missing usings
            return RefactoringResult.Succeeded(
                operationId,
                new FileChanges
                {
                    FilesModified = [],
                    FilesCreated = [],
                    FilesDeleted = []
                },
                null,
                0,
                0);
        }

        // Find candidate namespaces for each unresolved symbol
        var namespacesToAdd = new HashSet<string>();
        var compilation = semanticModel.Compilation;

        foreach (var diagnostic in diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            var typeName = GetTypeName(node);
            if (string.IsNullOrEmpty(typeName)) continue;

            // Search all assemblies for matching types
            var candidateNamespaces = FindNamespacesForType(compilation, typeName);
            if (candidateNamespaces.Count == 1)
            {
                namespacesToAdd.Add(candidateNamespaces[0]);
            }
            else if (candidateNamespaces.Count > 1)
            {
                // Take the most common/likely one (System namespaces first)
                var best = candidateNamespaces
                    .OrderBy(n => n.StartsWith("System") ? 0 : 1)
                    .ThenBy(n => n.Length)
                    .First();
                namespacesToAdd.Add(best);
            }
        }

        if (namespacesToAdd.Count == 0)
        {
            return RefactoringResult.Succeeded(
                operationId,
                new FileChanges
                {
                    FilesModified = [],
                    FilesCreated = [],
                    FilesDeleted = []
                },
                null,
                0,
                0);
        }

        // Get existing usings
        var existingUsings = root.Usings.Select(u => u.Name?.ToString() ?? "").ToHashSet();
        var newUsings = namespacesToAdd.Where(n => !existingUsings.Contains(n)).ToList();

        if (newUsings.Count == 0)
        {
            return RefactoringResult.Succeeded(
                operationId,
                new FileChanges
                {
                    FilesModified = [],
                    FilesCreated = [],
                    FilesDeleted = []
                },
                null,
                0,
                0);
        }

        // If preview mode, return without applying (but include before/after snippets)
        if (preview)
        {
            return CreatePreviewResult(operationId, sourceFile, newUsings, root);
        }

        // Add the using directives
        var newUsingDirectives = newUsings
            .Select(n => SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName(n).WithLeadingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
            .ToList();

        var allUsings = root.Usings.AddRange(newUsingDirectives);

        // Sort all usings using the standardized sorter
        var sortedUsings = UsingDirectiveSorter.Sort(allUsings);

        var newRoot = root.WithUsings(SyntaxFactory.List(sortedUsings));
        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = newDocument.Project.Solution;

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
            UsingDirectivesAdded = newUsings.Count
        };
    }

    /// <summary>
    /// Processes all C# documents in the solution to add missing using directives.
    /// Optional <c>sourceFile</c> limits the walk via
    /// <see cref="AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile"/>
    /// (default path; same as FormatDocument / ConvertToBlockBody).
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        AddMissingUsingsParams @params,
        CancellationToken cancellationToken)
    {
        // Accumulate on a local solution snapshot (same as SortUsings / FormatDocument)
        // so CommitChangesAsync can diff against the original Context.Solution.
        var currentSolution = Context.Solution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(currentSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            allDocuments = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);
        }

        // One physical path may appear as multiple Documents when linked into
        // several projects. Rewrite once per normalized path and apply the same
        // text to every sibling DocumentId (ConvertToBlockBody / Copilot).
        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var totalUsingsAdded = 0;
        var anyChanged = false;
        var allPendingChanges = new List<PendingChange>();

        foreach (var linkedDocuments in documentGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SourceText? changedText = null;
            List<string>? chosenUsings = null;
            CompilationUnitSyntax? previewRoot = null;
            Document? previewDocument = null;

            foreach (var linked in linkedDocuments)
            {
                var currentDocument = currentSolution.GetDocument(linked.Id) ?? linked;
                if (currentDocument is SourceGeneratedDocument)
                    continue;
                if (!DocumentEditableHelpers.IsDocumentEditable(currentDocument, Context.Workspace))
                    continue;

                var rewrite = await TryBuildMissingUsingsRewriteAsync(currentDocument, cancellationToken);
                if (rewrite is null)
                    continue;

                var (root, newRoot, newUsings) = rewrite.Value;
                var newDocument = currentDocument.WithSyntaxRoot(newRoot);
                var afterText = await newDocument.GetTextAsync(cancellationToken);

                if (changedText != null && !changedText.ContentEquals(afterText))
                {
                    throw new RefactoringException(
                        ErrorCodes.CannotConvert,
                        $"Linked workspace documents for '{currentDocument.FilePath}' produce different rewrites under current project contexts.");
                }

                changedText ??= afterText;
                chosenUsings ??= newUsings;
                previewRoot ??= root;
                previewDocument ??= currentDocument;
            }

            if (changedText == null || chosenUsings == null || previewRoot == null || previewDocument == null)
                continue;

            if (@params.Preview)
            {
                var previewResult = CreatePreviewResult(operationId, previewDocument.FilePath!, chosenUsings, previewRoot);
                if (previewResult.PendingChanges != null)
                    allPendingChanges.AddRange(previewResult.PendingChanges);
                totalUsingsAdded += chosenUsings.Count;
                continue;
            }

            foreach (var linked in linkedDocuments)
            {
                var sibling = currentSolution.GetDocument(linked.Id) ?? linked;
                if (sibling is SourceGeneratedDocument)
                    continue;
                if (!DocumentEditableHelpers.IsDocumentEditable(sibling, Context.Workspace))
                    continue;
                currentSolution = currentSolution.WithDocumentText(sibling.Id, changedText);
            }

            totalUsingsAdded += chosenUsings.Count;
            anyChanged = true;
        }

        // If preview mode, return aggregated preview
        if (@params.Preview)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = allPendingChanges,
                UsingDirectivesAdded = totalUsingsAdded
            };
        }

        // Commit all accumulated changes at once
        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
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
                UsingDirectivesAdded = totalUsingsAdded
            };
        }

        // No files needed changes
        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = [],
                FilesCreated = [],
                FilesDeleted = []
            },
            null,
            0,
            0);
    }

    /// <summary>
    /// Builds a missing-usings rewrite for one document, or null when no usings
    /// need to be added.
    /// </summary>
    private static async Task<(CompilationUnitSyntax Root, CompilationUnitSyntax NewRoot, List<string> NewUsings)?> TryBuildMissingUsingsRewriteAsync(
        Document document,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            return null;

        var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == DiagnosticIds.TypeOrNamespaceNotFound ||
                        d.Id == DiagnosticIds.NameDoesNotExist ||
                        d.Id == DiagnosticIds.TypeOrNamespaceDoesNotExistInNamespace)
            .ToList();

        if (diagnostics.Count == 0)
            return null;

        var namespacesToAdd = new HashSet<string>();
        var compilation = semanticModel.Compilation;

        foreach (var diagnostic in diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            var typeName = GetTypeName(node);
            if (string.IsNullOrEmpty(typeName)) continue;

            var candidateNamespaces = FindNamespacesForType(compilation, typeName);
            if (candidateNamespaces.Count == 1)
            {
                namespacesToAdd.Add(candidateNamespaces[0]);
            }
            else if (candidateNamespaces.Count > 1)
            {
                var best = candidateNamespaces
                    .OrderBy(n => n.StartsWith("System") ? 0 : 1)
                    .ThenBy(n => n.Length)
                    .First();
                namespacesToAdd.Add(best);
            }
        }

        if (namespacesToAdd.Count == 0)
            return null;

        var existingUsings = root.Usings.Select(u => u.Name?.ToString() ?? "").ToHashSet();
        var newUsings = namespacesToAdd.Where(n => !existingUsings.Contains(n)).ToList();
        if (newUsings.Count == 0)
            return null;

        var newUsingDirectives = newUsings
            .Select(n => SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName(n).WithLeadingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
            .ToList();

        var allUsingsForFile = root.Usings.AddRange(newUsingDirectives);
        var sortedUsings = UsingDirectiveSorter.Sort(allUsingsForFile);
        var newRoot = root.WithUsings(SyntaxFactory.List(sortedUsings));
        return (root, newRoot, newUsings);
    }

    private static string? GetTypeName(SyntaxNode node)
    {
        return node switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.ToString(),
            _ => null
        };
    }

    private static List<string> FindNamespacesForType(Compilation compilation, string typeName)
    {
        var namespaces = new List<string>();

        // Search in referenced assemblies
        foreach (var reference in compilation.References)
        {
            var assembly = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
            if (assembly == null) continue;

            var types = GetAllTypes(assembly.GlobalNamespace)
                .Where(t => t.Name == typeName && t.DeclaredAccessibility == Accessibility.Public)
                .ToList();

            foreach (var type in types)
            {
                var ns = type.ContainingNamespace.ToDisplayString();
                if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
                {
                    namespaces.Add(ns);
                }
            }
        }

        // Search in current compilation
        var localTypes = GetAllTypes(compilation.GlobalNamespace)
            .Where(t => t.Name == typeName)
            .ToList();

        foreach (var type in localTypes)
        {
            var ns = type.ContainingNamespace.ToDisplayString();
            if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
            {
                namespaces.Add(ns);
            }
        }

        return namespaces.Distinct().ToList();
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(childNs))
            {
                yield return type;
            }
        }
    }

    /// <summary>
    /// Creates a preview result with before/after using directive snippets.
    /// </summary>
    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string filePath,
        List<string> namespacesToAdd,
        CompilationUnitSyntax root)
    {
        // Build the "before" snippet showing existing usings
        var existingUsings = root.Usings.Select(u => u.ToString().Trim()).ToList();
        var beforeSnippet = existingUsings.Count > 0
            ? string.Join(Environment.NewLine, existingUsings)
            : "// No using directives";

        // Build the "after" snippet showing what will be added
        var newUsingsText = namespacesToAdd.Select(n => $"using {n};").ToList();
        var afterSnippet = string.Join(Environment.NewLine, existingUsings.Concat(newUsingsText));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = filePath,
                ChangeType = ChangeKind.Modify,
                Description = $"Add {namespacesToAdd.Count} using directive(s): {string.Join(", ", namespacesToAdd)}",
                StartLine = 1,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = afterSnippet
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

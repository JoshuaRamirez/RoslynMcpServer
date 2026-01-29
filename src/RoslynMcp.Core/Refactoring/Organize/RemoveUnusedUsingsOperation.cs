using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Organize.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Organize;

/// <summary>
/// Removes unused using directives from a file.
/// </summary>
public sealed class RemoveUnusedUsingsOperation : RefactoringOperationBase<RemoveUnusedUsingsParams>
{
    /// <summary>
    /// Creates a new remove unused usings operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public RemoveUnusedUsingsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RemoveUnusedUsingsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RemoveUnusedUsingsParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find unused usings via diagnostics using defined diagnostic IDs
        var unusedUsingDiagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == DiagnosticIds.UnnecessaryUsing ||
                        d.Id == DiagnosticIds.UnnecessaryUsingIde)
            .ToList();

        // Also do semantic analysis for unused usings
        var usedNamespaces = GetUsedNamespaces(root, semanticModel, cancellationToken);
        var unusedUsings = new List<UsingDirectiveSyntax>();

        foreach (var usingDirective in root.Usings)
        {
            var namespaceName = usingDirective.Name?.ToString();
            if (namespaceName == null) continue;

            // Check if this using is in the diagnostics
            var isUnused = unusedUsingDiagnostics.Any(d =>
                d.Location.SourceSpan.IntersectsWith(usingDirective.Span));

            // Or check if it's not in our used namespaces
            if (!isUnused && !usedNamespaces.Contains(namespaceName))
            {
                // Do a more thorough check - see if any type from this namespace is used
                var nsSymbol = semanticModel.Compilation.GlobalNamespace
                    .GetNamespaceMembers()
                    .FirstOrDefault(n => n.ToDisplayString() == namespaceName);

                if (nsSymbol == null)
                {
                    // Namespace not found in compilation, might be unused
                    isUnused = true;
                }
            }

            if (isUnused)
            {
                unusedUsings.Add(usingDirective);
            }
        }

        // Also collect from diagnostics directly
        foreach (var diagnostic in unusedUsingDiagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            if (node is UsingDirectiveSyntax usingNode && !unusedUsings.Contains(usingNode))
            {
                unusedUsings.Add(usingNode);
            }
        }

        if (unusedUsings.Count == 0)
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
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params.SourceFile, unusedUsings, root);
        }

        // Remove unused usings and re-sort the remaining ones
        var remainingUsings = root.Usings
            .Where(u => !unusedUsings.Contains(u))
            .ToList();

        // Sort remaining usings using the standardized sorter
        var sortedUsings = UsingDirectiveSorter.Sort(remainingUsings);

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
            UsingDirectivesRemoved = unusedUsings.Count
        };
    }

    /// <summary>
    /// Collects all namespaces that are actually used in the file.
    /// </summary>
    /// <param name="root">Compilation unit to analyze.</param>
    /// <param name="semanticModel">Semantic model for symbol resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Set of namespace names that are used.</returns>
    /// <remarks>
    /// Properly detects extension method namespaces by analyzing InvocationExpressions
    /// using GetSymbolInfo, not just IdentifierNameSyntax nodes.
    /// </remarks>
    private static HashSet<string> GetUsedNamespaces(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var usedNamespaces = new HashSet<string>();

        // Walk all identifier names and get their containing namespaces
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

            if (symbol != null)
            {
                AddNamespaceFromSymbol(usedNamespaces, symbol);
            }
        }

        // Check invocation expressions specifically to detect extension method calls
        // Extension methods appear as method calls on an instance but resolve to static methods
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

            if (symbol is IMethodSymbol method)
            {
                // Add the method's containing namespace
                AddNamespaceFromSymbol(usedNamespaces, method);

                // For extension methods, also add the namespace of the static class containing the method
                if (method.IsExtensionMethod)
                {
                    var containingType = method.ContainingType;
                    if (containingType?.ContainingNamespace != null &&
                        !containingType.ContainingNamespace.IsGlobalNamespace)
                    {
                        usedNamespaces.Add(containingType.ContainingNamespace.ToDisplayString());
                    }

                    // Also check for reduced extension method (the original definition)
                    var reducedFrom = method.ReducedFrom;
                    if (reducedFrom?.ContainingType?.ContainingNamespace != null &&
                        !reducedFrom.ContainingType.ContainingNamespace.IsGlobalNamespace)
                    {
                        usedNamespaces.Add(reducedFrom.ContainingType.ContainingNamespace.ToDisplayString());
                    }
                }
            }
        }

        // Also check type syntax
        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(typeSyntax, cancellationToken);
            if (typeInfo.Type?.ContainingNamespace != null &&
                !typeInfo.Type.ContainingNamespace.IsGlobalNamespace)
            {
                usedNamespaces.Add(typeInfo.Type.ContainingNamespace.ToDisplayString());
            }
        }

        return usedNamespaces;
    }

    /// <summary>
    /// Adds the containing namespace of a symbol to the used namespaces set.
    /// </summary>
    private static void AddNamespaceFromSymbol(HashSet<string> usedNamespaces, ISymbol symbol)
    {
        var containingNamespace = symbol.ContainingNamespace;
        if (containingNamespace != null && !containingNamespace.IsGlobalNamespace)
        {
            usedNamespaces.Add(containingNamespace.ToDisplayString());
        }

        // For extension methods on IMethodSymbol, add containing type's namespace
        if (symbol is IMethodSymbol method && method.IsExtensionMethod)
        {
            var containingType = method.ContainingType;
            if (containingType?.ContainingNamespace != null &&
                !containingType.ContainingNamespace.IsGlobalNamespace)
            {
                usedNamespaces.Add(containingType.ContainingNamespace.ToDisplayString());
            }
        }
    }

    /// <summary>
    /// Creates a preview result with before/after using directive snippets.
    /// </summary>
    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string filePath,
        List<UsingDirectiveSyntax> unusedUsings,
        CompilationUnitSyntax root)
    {
        var namespaces = unusedUsings.Select(u => u.Name?.ToString() ?? "unknown").ToList();

        // Build the "before" snippet showing all usings
        var allUsings = root.Usings.Select(u => u.ToString().Trim()).ToList();
        var beforeSnippet = string.Join(Environment.NewLine, allUsings);

        // Build the "after" snippet showing usings without the unused ones
        var unusedSet = new HashSet<UsingDirectiveSyntax>(unusedUsings);
        var remainingUsings = root.Usings
            .Where(u => !unusedSet.Contains(u))
            .Select(u => u.ToString().Trim())
            .ToList();
        var afterSnippet = remainingUsings.Count > 0
            ? string.Join(Environment.NewLine, remainingUsings)
            : "// All using directives removed";

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = filePath,
                ChangeType = ChangeKind.Modify,
                Description = $"Remove {unusedUsings.Count} unused using directive(s): {string.Join(", ", namespaces)}",
                StartLine = 1,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = afterSnippet
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

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
        AddMissingUsingsParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
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
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params.SourceFile, newUsings, root);
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

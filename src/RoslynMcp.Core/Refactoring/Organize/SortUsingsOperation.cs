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
/// Sorts using directives alphabetically in a C# file.
/// </summary>
public sealed class SortUsingsOperation : RefactoringOperationBase<SortUsingsParams>
{
    /// <summary>
    /// Creates a new sort usings operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public SortUsingsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(SortUsingsParams @params)
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
        SortUsingsParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;

        if (root == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // If there are no usings or only one, nothing to sort
        if (root.Usings.Count <= 1)
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

        // Sort usings using the standardized sorter
        var sortedUsings = UsingDirectiveSorter.Sort(root.Usings);

        // Check if the order actually changed
        var alreadySorted = root.Usings
            .Select(u => u.ToString())
            .SequenceEqual(sortedUsings.Select(u => u.ToString()));

        if (alreadySorted)
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

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params.SourceFile, root, sortedUsings);
        }

        // Apply the sorted usings
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
            }
        };
    }

    /// <summary>
    /// Creates a preview result with before/after using directive snippets.
    /// </summary>
    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string filePath,
        CompilationUnitSyntax root,
        List<UsingDirectiveSyntax> sortedUsings)
    {
        var beforeSnippet = string.Join(Environment.NewLine,
            root.Usings.Select(u => u.ToString().Trim()));
        var afterSnippet = string.Join(Environment.NewLine,
            sortedUsings.Select(u => u.ToString().Trim()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = filePath,
                ChangeType = ChangeKind.Modify,
                Description = "Sort using directives alphabetically",
                StartLine = 1,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = afterSnippet
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

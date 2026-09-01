using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Format;

/// <summary>
/// Formats a document using Roslyn's Formatter.FormatAsync().
/// </summary>
public sealed class FormatDocumentOperation : RefactoringOperationBase<FormatDocumentParams>
{
    /// <inheritdoc />
    public FormatDocumentOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FormatDocumentParams @params)
    {
        if (@params.AllFiles)
        {
            // When processing all files, sourceFile is optional
            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required when allFiles is false.");

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
        FormatDocumentParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
        {
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);
        }

        return await ExecuteSingleFileAsync(operationId, @params.SourceFile!, @params.Preview, cancellationToken);
    }

    /// <summary>
    /// Formats a single document.
    /// </summary>
    private async Task<RefactoringResult> ExecuteSingleFileAsync(
        Guid operationId,
        string sourceFile,
        bool preview,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(sourceFile);
        var formattedDocument = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);

        if (preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                sourceFile,
                document,
                formattedDocument,
                cancellationToken);
        }

        var newSolution = formattedDocument.Project.Solution;

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

    /// <summary>
    /// Formats every C# document in the solution.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        FormatDocumentParams @params,
        CancellationToken cancellationToken)
    {
        var currentSolution = Context.Solution;
        var allDocuments = currentSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDocument = currentSolution.GetDocument(document.Id) ?? document;
            var formattedDocument = await Formatter.FormatAsync(currentDocument, cancellationToken: cancellationToken);

            var before = (await currentDocument.GetTextAsync(cancellationToken)).ToString();
            var after = (await formattedDocument.GetTextAsync(cancellationToken)).ToString();

            if (before == after)
                continue;

            if (@params.Preview)
            {
                var previewResult = await CreatePreviewResultAsync(
                    operationId,
                    currentDocument.FilePath!,
                    currentDocument,
                    formattedDocument,
                    cancellationToken);
                if (previewResult.PendingChanges != null)
                    allPendingChanges.AddRange(previewResult.PendingChanges);
                continue;
            }

            currentSolution = formattedDocument.Project.Solution;
            anyChanged = true;
        }

        if (@params.Preview)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = allPendingChanges
            };
        }

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
                }
            };
        }

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
    /// Builds a preview result from the original vs formatted document text without writing.
    /// </summary>
    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        string filePath,
        Document originalDocument,
        Document formattedDocument,
        CancellationToken cancellationToken)
    {
        var before = (await originalDocument.GetTextAsync(cancellationToken)).ToString();
        var after = (await formattedDocument.GetTextAsync(cancellationToken)).ToString();

        if (before == after)
        {
            return RefactoringResult.PreviewResult(operationId, []);
        }

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = filePath,
                ChangeType = ChangeKind.Modify,
                Description = "Format document according to conventions",
                StartLine = 1,
                BeforeSnippet = before,
                AfterSnippet = after
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

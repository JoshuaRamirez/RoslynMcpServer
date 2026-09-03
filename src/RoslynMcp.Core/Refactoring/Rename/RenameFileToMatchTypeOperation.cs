using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Renames a source file so its name matches the primary type declared in it.
/// Does not rename the type, constructors, or references.
/// </summary>
public sealed class RenameFileToMatchTypeOperation : RefactoringOperationBase<RenameFileToMatchTypeParams>
{
    /// <summary>
    /// Creates a new rename-file-to-match-type operation.
    /// </summary>
    public RenameFileToMatchTypeOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RenameFileToMatchTypeParams @params) => Validate(@params);

    /// <summary>
    /// Validates rename-file-to-match-type inputs. Internal so tests can exercise
    /// rules without loading a workspace.
    /// </summary>
    internal static void Validate(RenameFileToMatchTypeParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.TypeName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with typeName, line, or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <summary>
    /// Rejects documents that cannot receive path or text edits.
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

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument) &&
            !workspace.CanApplyChange(ApplyChangesKind.ChangeDocumentInfo))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <summary>
    /// True when the document can receive a path or text edit. AllFiles skips
    /// rather than throwing the single-file <see cref="ErrorCodes.DocumentNotEditable"/>.
    /// </summary>
    internal static bool IsDocumentEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
            return false;

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
            return false;

        return workspace.CanApplyChange(ApplyChangesKind.ChangeDocument)
               || workspace.CanApplyChange(ApplyChangesKind.ChangeDocumentInfo);
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RenameFileToMatchTypeParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var document = GetDocumentOrThrow(sourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var types = FindTopLevelTypes(root);
        var selected = ResolvePrimaryType(types, @params);
        var typeName = selected.Name;
        var newFilePath = GetTargetFilePath(sourceFile, typeName);

        if (FileNameMatchesType(sourceFile, typeName))
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                $"File already matches type '{typeName}'.");
        }

        if (IsDestinationOccupiedByDifferentFile(sourceFile, newFilePath))
        {
            throw new RefactoringException(
                ErrorCodes.TargetFileExists,
                $"Destination file already exists: {newFilePath}");
        }

        var symbol = semanticModel.GetDeclaredSymbol(selected.Node, cancellationToken);
        var projectPath = document.Project.FilePath;
        var updatedProjectText = TryGetUpdatedProjectText(projectPath, sourceFile, newFilePath);

        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                sourceFile,
                newFilePath,
                typeName,
                updatedProjectText != null ? projectPath : null);
        }

        try
        {
            MoveSourceFile(sourceFile, newFilePath);
        }
        catch (IOException ex)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to rename file: {ex.Message}",
                ex);
        }

        if (updatedProjectText != null && !string.IsNullOrWhiteSpace(projectPath))
        {
            try
            {
                File.WriteAllText(projectPath, updatedProjectText);
            }
            catch (IOException ex)
            {
                throw new RefactoringException(
                    ErrorCodes.FilesystemError,
                    $"Failed to update project file: {ex.Message}",
                    ex);
            }
        }

        var newSolution = Context.Solution.WithDocumentFilePath(document.Id, newFilePath);
        Context.UpdateSolution(newSolution);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = updatedProjectText != null && projectPath != null ? [projectPath] : [],
                FilesCreated = [newFilePath],
                FilesDeleted = [sourceFile]
            },
            CreateSymbolInfo(symbol, typeName, selected.Node, sourceFile, newFilePath),
            0,
            0);
    }

    /// <summary>
    /// Renames every C# document in the solution whose file name does not match
    /// its single top-level type (same document filter as
    /// <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>:
    /// <c>FilePath != null &amp;&amp; EndsWith(".cs")</c>).
    /// Multi-type, zero-type, already-matching, destination-occupied, and
    /// uneditable documents are skipped. A physical file linked into several
    /// projects is moved once; every owning document and project still receives
    /// workspace / compile-item updates. Destination collisions follow
    /// <see cref="ReferToSameFile"/> filesystem semantics (case-sensitive
    /// volumes keep distinct casings) and skip both claimants. A mid-batch
    /// move failure rolls back completed moves so a half-applied rename is
    /// never left on disk.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        RenameFileToMatchTypeParams @params,
        CancellationToken cancellationToken)
    {
        var allDocuments = Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = new List<FileRenamePlan>();
        var skippedSources = new List<string>();
        var caseDistinctCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceFile = document.FilePath;
            if (string.IsNullOrWhiteSpace(sourceFile))
                continue;

            var existingPlan = FindPlanForSource(candidates, sourceFile);
            if (existingPlan != null)
            {
                existingPlan.AddDocument(document);
                continue;
            }

            if (skippedSources.Any(s => ReferToSameFile(s, sourceFile)))
                continue;

            if (!IsDocumentEditable(document, Context.Workspace))
            {
                skippedSources.Add(sourceFile);
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                skippedSources.Add(sourceFile);
                continue;
            }

            var types = FindTopLevelTypes(root);
            if (types.Count != 1)
            {
                skippedSources.Add(sourceFile);
                continue;
            }

            var typeName = types[0].Name;
            if (FileNameMatchesType(sourceFile, typeName))
            {
                skippedSources.Add(sourceFile);
                continue;
            }

            var destinationFile = GetTargetFilePath(sourceFile, typeName);
            if (IsDestinationOccupiedByDifferentFile(sourceFile, destinationFile))
            {
                skippedSources.Add(sourceFile);
                continue;
            }

            candidates.Add(new FileRenamePlan(sourceFile, destinationFile, typeName, document));
        }

        var colliding = new HashSet<FileRenamePlan>();
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (!DestinationsReferToSameLocation(
                        candidates[i].DestinationFile,
                        candidates[j].DestinationFile,
                        caseDistinctCache))
                    continue;

                colliding.Add(candidates[i]);
                colliding.Add(candidates[j]);
            }
        }

        var plans = candidates.Where(p => !colliding.Contains(p)).ToList();

        var projectUpdates = CollectProjectUpdates(plans);

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>();
            foreach (var plan in plans)
            {
                AddRenamePendingChanges(
                    pendingChanges,
                    plan.SourceFile,
                    plan.DestinationFile,
                    plan.TypeName,
                    projectPath: null);
            }

            foreach (var projectPath in projectUpdates.Keys)
            {
                pendingChanges.Add(new PendingChange
                {
                    File = projectPath,
                    ChangeType = ChangeKind.Modify,
                    Description = "Update explicit Compile item to the renamed file"
                });
            }

            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        if (plans.Count == 0)
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

        var filesCreated = new List<string>();
        var filesDeleted = new List<string>();
        var newSolution = Context.Solution;

        try
        {
            ApplyFileMovesOrRollback(plans.Select(p => (p.SourceFile, p.DestinationFile)).ToList());
            foreach (var plan in plans)
            {
                foreach (var document in plan.Documents)
                    newSolution = newSolution.WithDocumentFilePath(document.Id, plan.DestinationFile);
                filesCreated.Add(plan.DestinationFile);
                filesDeleted.Add(plan.SourceFile);
            }
        }
        catch (IOException ex)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to rename file: {ex.Message}",
                ex);
        }

        var filesModified = new List<string>();
        foreach (var (projectPath, updatedProjectText) in projectUpdates)
        {
            try
            {
                File.WriteAllText(projectPath, updatedProjectText);
                filesModified.Add(projectPath);
            }
            catch (IOException ex)
            {
                throw new RefactoringException(
                    ErrorCodes.FilesystemError,
                    $"Failed to update project file: {ex.Message}",
                    ex);
            }
        }

        Context.UpdateSolution(newSolution);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted
            },
            null,
            0,
            0);
    }

    /// <summary>
    /// Top-level named types in a file (class, struct, interface, record, enum, delegate).
    /// Nested types are ignored, matching <c>move_type_to_file</c>.
    /// </summary>
    internal static IReadOnlyList<TypeTarget> FindTopLevelTypes(SyntaxNode root)
    {
        return root.DescendantNodes()
            .Where(IsNamedTypeDeclaration)
            .Where(n => n.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Select(n => new TypeTarget(GetDeclaredTypeName(n), n))
            .ToList();
    }

    /// <summary>
    /// Picks the type that the file should be renamed to match.
    /// A single top-level type is used automatically; more than one requires
    /// <see cref="RenameFileToMatchTypeParams.TypeName"/> or a location.
    /// </summary>
    internal static TypeTarget ResolvePrimaryType(
        IReadOnlyList<TypeTarget> types,
        RenameFileToMatchTypeParams @params)
    {
        if (types.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                "No type found in file.");
        }

        var typeName = string.IsNullOrWhiteSpace(@params.TypeName) ? null : @params.TypeName.Trim();
        IReadOnlyList<TypeTarget> candidates = types;

        if (typeName != null)
        {
            candidates = types.Where(t => t.Name == typeName).ToList();
            if (candidates.Count == 0)
            {
                throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"No type named '{typeName}' found in file.",
                    new Dictionary<string, object>
                    {
                        ["availableTypes"] = types.Select(t => t.Name).ToList()
                    });
            }

            if (candidates.Count == 1)
                return candidates[0];
        }

        // Column without Line is not a source position: do not invent
        // column-only cross-line disambiguation or substitute each
        // candidate's own start line.
        if (@params.Line.HasValue)
        {
            if (@params.Column.HasValue)
            {
                // Exclusive-end covering-span: prefer the type identifier
                // that covers the column, then the smallest covering
                // declaration. Do not throw SymbolAmbiguous when that
                // uniquely selects.
                var covering = candidates
                    .Select(t => (
                        Target: t,
                        IdentifierHit: IdentifierCoversColumn(t.Node, @params.Line.Value, @params.Column.Value),
                        DeclarationHit: SpanCoversColumn(
                            t.Node.GetLocation().GetLineSpan(), @params.Line.Value, @params.Column.Value)))
                    .Where(t => t.IdentifierHit || t.DeclarationHit)
                    .OrderBy(t => t.IdentifierHit ? 0 : 1)
                    .ThenBy(t => t.Target.Node.Span.Length)
                    .ToList();

                if (covering.Count == 0)
                {
                    throw new RefactoringException(
                        ErrorCodes.TypeNotFound,
                        typeName == null
                            ? $"No type found at line {@params.Line}."
                            : $"No type named '{typeName}' found at line {@params.Line}.");
                }

                return covering[0].Target;
            }

            // Omitted column keeps today's typeName + optional line
            // pick exactly. SpanCoversLine without a column is multi-line
            // declaration coverage — do not force column 1.
            var atLocation = candidates
                .Where(t => SpanCoversLine(t.Node.GetLocation().GetLineSpan(), @params.Line.Value, column: null))
                .ToList();

            if (atLocation.Count == 1)
                return atLocation[0];

            if (atLocation.Count == 0)
            {
                throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    typeName == null
                        ? $"No type found at line {@params.Line}."
                        : $"No type named '{typeName}' found at line {@params.Line}.");
            }

            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple types found at line {@params.Line}. Provide column or typeName.");
        }

        if (candidates.Count == 1)
            return candidates[0];

        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            "Multiple types found in file. Provide typeName or line to disambiguate.",
            new Dictionary<string, object>
            {
                ["candidateCount"] = candidates.Count,
                ["availableTypes"] = candidates.Select(t => t.Name).ToList()
            },
            ["Provide typeName", "Provide line (and column if needed)"]);
    }

    /// <summary>
    /// Builds the destination path in the same directory as <paramref name="sourceFile"/>.
    /// </summary>
    internal static string GetTargetFilePath(string sourceFile, string typeName)
    {
        var directory = Path.GetDirectoryName(sourceFile)
            ?? throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile has no directory.");
        var extension = Path.GetExtension(sourceFile);
        if (string.IsNullOrEmpty(extension))
            extension = ".cs";
        return Path.Combine(directory, typeName + extension);
    }

    /// <summary>
    /// True when the file name (without extension) already equals the type name.
    /// </summary>
    internal static bool FileNameMatchesType(string sourceFile, string typeName) =>
        string.Equals(Path.GetFileNameWithoutExtension(sourceFile), typeName, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="newFilePath"/> exists as a different file than
    /// <paramref name="sourceFile"/>. Case-only paths on a case-insensitive volume
    /// are the same file and are not a conflict.
    /// </summary>
    internal static bool IsDestinationOccupiedByDifferentFile(string sourceFile, string newFilePath)
    {
        if (!File.Exists(newFilePath))
            return false;
        return !ReferToSameFile(sourceFile, newFilePath);
    }

    /// <summary>
    /// True when both paths identify the same existing file, including
    /// case-only differences on a case-insensitive volume.
    /// </summary>
    internal static bool ReferToSameFile(string left, string right)
    {
        var leftFull = PathResolver.NormalizePath(left);
        var rightFull = PathResolver.NormalizePath(right);
        if (string.Equals(leftFull, rightFull, StringComparison.Ordinal))
            return true;

        if (!string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(leftFull) || !File.Exists(rightFull))
            return false;

        var dir = Path.GetDirectoryName(leftFull);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return OperatingSystem.IsWindows();

        var name = Path.GetFileName(leftFull);
        var distinctMatches = Directory.EnumerateFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => n != null && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();
        return distinctMatches == 1;
    }

    /// <summary>
    /// True when the paths are the same location with different casing.
    /// </summary>
    internal static bool PathsDifferOnlyByCase(string left, string right)
    {
        var leftFull = PathResolver.NormalizePath(left);
        var rightFull = PathResolver.NormalizePath(right);
        return !string.Equals(leftFull, rightFull, StringComparison.Ordinal)
               && string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves <paramref name="sourceFile"/> to <paramref name="newFilePath"/>.
    /// Case-only renames go through a temporary name so case-insensitive
    /// volumes actually change the on-disk casing.
    /// </summary>
    internal static void MoveSourceFile(string sourceFile, string newFilePath)
    {
        if (PathsDifferOnlyByCase(sourceFile, newFilePath))
        {
            var directory = Path.GetDirectoryName(sourceFile)
                ?? throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile has no directory.");
            string tempPath;
            do
            {
                tempPath = Path.Combine(directory, $".roslynmcp_rename_{Guid.NewGuid():N}.cs");
            } while (File.Exists(tempPath));

            File.Move(sourceFile, tempPath);
            try
            {
                File.Move(tempPath, newFilePath);
            }
            catch
            {
                if (File.Exists(tempPath) && !File.Exists(sourceFile))
                    File.Move(tempPath, sourceFile);
                throw;
            }

            return;
        }

        File.Move(sourceFile, newFilePath);
    }

    /// <summary>
    /// Rewrites explicit <c>Compile Include</c>/<c>Update</c> items that point at
    /// <paramref name="sourceFile"/> so they point at <paramref name="newFilePath"/>.
    /// Returns the original text when no matching items exist.
    /// </summary>
    internal static string UpdateExplicitCompileItems(
        string projectXml,
        string projectDirectory,
        string sourceFile,
        string newFilePath)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(projectXml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException)
        {
            return projectXml;
        }

        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var newFileName = Path.GetFileName(newFilePath);
        var changed = false;

        foreach (var compile in document.Descendants(ns + "Compile").ToList())
        {
            changed |= TryRewriteProjectItemAttribute(compile, "Include", projectDirectory, sourceFile, newFileName);
            changed |= TryRewriteProjectItemAttribute(compile, "Update", projectDirectory, sourceFile, newFileName);
        }

        if (!changed)
            return projectXml;

        return SerializeProjectXml(document, projectXml);
    }

    /// <summary>
    /// True when a project item path (relative or absolute) refers to <paramref name="sourceFile"/>.
    /// </summary>
    internal static bool ProjectItemRefersToFile(string projectDirectory, string includeOrUpdate, string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(includeOrUpdate))
            return false;

        string resolved;
        try
        {
            var withNativeSeps = includeOrUpdate.Trim()
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            resolved = Path.IsPathRooted(withNativeSeps)
                ? PathResolver.NormalizePath(withNativeSeps)
                : PathResolver.NormalizePath(Path.Combine(projectDirectory, withNativeSeps));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var sourceNorm = PathResolver.NormalizePath(sourceFile);
        if (string.Equals(resolved, sourceNorm, StringComparison.Ordinal))
            return true;

        return ReferToSameFile(resolved, sourceNorm);
    }

    /// <summary>
    /// Replaces the file-name segment of a project item path, preserving directory
    /// and separator style (<c>Foo.cs</c> → <c>Bar.cs</c>, <c>src\Foo.cs</c> → <c>src\Bar.cs</c>).
    /// </summary>
    internal static string RewriteProjectItemPath(string includeOrUpdate, string newFileName)
    {
        var lastSlash = Math.Max(includeOrUpdate.LastIndexOf('/'), includeOrUpdate.LastIndexOf('\\'));
        return lastSlash < 0
            ? newFileName
            : includeOrUpdate[..(lastSlash + 1)] + newFileName;
    }

    internal static bool IsNamedTypeDeclaration(SyntaxNode node) =>
        node is TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax;

    internal static string GetDeclaredTypeName(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax named => named.Identifier.ValueText,
        DelegateDeclarationSyntax del => del.Identifier.ValueText,
        _ => throw new RefactoringException(ErrorCodes.RoslynError, "Node is not a named type declaration.")
    };

    internal static bool SpanCoversLine(FileLinePositionSpan span, int line, int? column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        if (line < startLine || line > endLine)
            return false;

        if (!column.HasValue)
            return true;

        return SpanCoversColumn(span, line, column.Value);
    }

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent type also
    /// match the previous declaration. Same helper as
    /// <c>TypeSymbolResolver.SpanCoversColumn</c> /
    /// <c>RenameNamespaceOperation.SpanCoversColumn</c> /
    /// <c>InlineMethodOperation.SpanCoversColumn</c>.
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

    private static bool IdentifierCoversColumn(SyntaxNode node, int line, int column)
    {
        var identifier = GetTypeIdentifier(node);
        return identifier != default
               && SpanCoversColumn(identifier.GetLocation().GetLineSpan(), line, column);
    }

    internal static SyntaxToken GetTypeIdentifier(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax named => named.Identifier,
        DelegateDeclarationSyntax del => del.Identifier,
        _ => default
    };

    internal readonly record struct TypeTarget(string Name, SyntaxNode Node);

    private static string? TryGetUpdatedProjectText(string? projectPath, string sourceFile, string newFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return null;

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(projectDirectory))
            return null;

        var original = File.ReadAllText(projectPath);
        var updated = UpdateExplicitCompileItems(original, projectDirectory, sourceFile, newFilePath);
        return string.Equals(original, updated, StringComparison.Ordinal) ? null : updated;
    }

    private static bool TryRewriteProjectItemAttribute(
        XElement compile,
        string attributeName,
        string projectDirectory,
        string sourceFile,
        string newFileName)
    {
        var attribute = compile.Attribute(attributeName);
        if (attribute == null || !ProjectItemRefersToFile(projectDirectory, attribute.Value, sourceFile))
            return false;

        var rewritten = RewriteProjectItemPath(attribute.Value, newFileName);
        if (string.Equals(attribute.Value, rewritten, StringComparison.Ordinal))
            return false;

        attribute.Value = rewritten;
        return true;
    }

    private static string SerializeProjectXml(XDocument document, string originalXml)
    {
        var writerSettings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = !originalXml.Contains("<?xml", StringComparison.OrdinalIgnoreCase),
            NewLineHandling = System.Xml.NewLineHandling.Replace,
            NewLineChars = originalXml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            Indent = false
        };

        using var writer = new StringWriter();
        using (var xmlWriter = System.Xml.XmlWriter.Create(writer, writerSettings))
        {
            document.Save(xmlWriter);
        }

        var serialized = writer.ToString();
        if (originalXml.EndsWith('\n') && !serialized.EndsWith('\n'))
            serialized += writerSettings.NewLineChars;
        return serialized;
    }

    private static Dictionary<string, string> CollectProjectUpdates(IReadOnlyList<FileRenamePlan> plans)
    {
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plan in plans)
        {
            foreach (var document in plan.Documents)
            {
                var projectPath = document.Project.FilePath;
                if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
                    continue;

                var projectDirectory = Path.GetDirectoryName(projectPath);
                if (string.IsNullOrEmpty(projectDirectory))
                    continue;

                if (!originals.TryGetValue(projectPath, out var original))
                {
                    original = File.ReadAllText(projectPath);
                    originals[projectPath] = original;
                }

                var current = updates.TryGetValue(projectPath, out var pending) ? pending : original;
                var updated = UpdateExplicitCompileItems(
                    current,
                    projectDirectory,
                    plan.SourceFile,
                    plan.DestinationFile);
                if (!string.Equals(current, updated, StringComparison.Ordinal))
                    updates[projectPath] = updated;
            }
        }

        return updates;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string sourceFile,
        string newFilePath,
        string typeName,
        string? projectPath)
    {
        var pendingChanges = new List<PendingChange>();
        AddRenamePendingChanges(pendingChanges, sourceFile, newFilePath, typeName, projectPath);
        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static void AddRenamePendingChanges(
        List<PendingChange> pendingChanges,
        string sourceFile,
        string newFilePath,
        string typeName,
        string? projectPath)
    {
        pendingChanges.Add(new PendingChange
        {
            File = sourceFile,
            ChangeType = ChangeKind.Delete,
            Description = $"Rename file to match type '{typeName}'"
        });
        pendingChanges.Add(new PendingChange
        {
            File = newFilePath,
            ChangeType = ChangeKind.Create,
            Description = $"Rename '{Path.GetFileName(sourceFile)}' to '{Path.GetFileName(newFilePath)}'"
        });

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            pendingChanges.Add(new PendingChange
            {
                File = projectPath,
                ChangeType = ChangeKind.Modify,
                Description = "Update explicit Compile item to the renamed file"
            });
        }
    }

    /// <summary>
    /// Moves each pair in order. If a later move throws, completed moves are
    /// rolled back so the batch never leaves a half-applied rename on disk.
    /// </summary>
    internal static void ApplyFileMovesOrRollback(
        IReadOnlyList<(string SourceFile, string DestinationFile)> moves)
    {
        var completed = new List<(string SourceFile, string DestinationFile)>();
        try
        {
            foreach (var move in moves)
            {
                MoveSourceFile(move.SourceFile, move.DestinationFile);
                completed.Add(move);
            }
        }
        catch (IOException)
        {
            RollbackCompletedMoves(completed);
            throw;
        }
    }

    /// <summary>
    /// Restores completed moves in reverse order. Best-effort: a rollback
    /// failure is swallowed so the original <see cref="IOException"/> can
    /// propagate as <see cref="ErrorCodes.FilesystemError"/>.
    /// </summary>
    internal static void RollbackCompletedMoves(
        IReadOnlyList<(string SourceFile, string DestinationFile)> completed)
    {
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var (source, dest) = completed[i];
            try
            {
                if (File.Exists(dest))
                    MoveSourceFile(dest, source);
            }
            catch
            {
                // Best-effort rollback.
            }
        }
    }

    /// <summary>
    /// True when two destination paths would occupy the same filesystem
    /// location, including case-only pairs on a case-insensitive volume.
    /// Distinct casings on a case-sensitive volume are not a collision.
    /// </summary>
    internal static bool DestinationsReferToSameLocation(
        string left,
        string right,
        IDictionary<string, bool>? caseDistinctCache = null)
    {
        var leftFull = PathResolver.NormalizePath(left);
        var rightFull = PathResolver.NormalizePath(right);
        if (string.Equals(leftFull, rightFull, StringComparison.Ordinal))
            return true;

        if (!string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase))
            return false;

        if (File.Exists(leftFull) && File.Exists(rightFull))
            return ReferToSameFile(leftFull, rightFull);

        var directory = Path.GetDirectoryName(leftFull);
        return !DirectoryTreatsCaseAsDistinct(directory, caseDistinctCache);
    }

    /// <summary>
    /// True when <paramref name="directory"/> stores <c>Foo</c> and <c>foo</c>
    /// as different files. Probes once per directory (optional cache).
    /// </summary>
    internal static bool DirectoryTreatsCaseAsDistinct(
        string? directory,
        IDictionary<string, bool>? cache = null)
    {
        if (string.IsNullOrEmpty(directory))
            return !OperatingSystem.IsWindows();

        var cacheKey = PathResolver.NormalizePath(directory);
        if (cache != null && cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var distinct = ProbeDirectoryTreatsCaseAsDistinct(directory);
        cache?.Add(cacheKey, distinct);
        return distinct;
    }

    private static bool ProbeDirectoryTreatsCaseAsDistinct(string directory)
    {
        if (!Directory.Exists(directory))
            return !OperatingSystem.IsWindows();

        var name = $".roslynmcp_case_{Guid.NewGuid():N}";
        var lower = Path.Combine(directory, name);
        var upper = Path.Combine(directory, name.ToUpperInvariant());
        File.WriteAllBytes(lower, []);
        try
        {
            if (!File.Exists(upper))
                return true;

            var distinctMatches = Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(n => n != null && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .Count();
            return distinctMatches > 1;
        }
        finally
        {
            if (File.Exists(lower))
                File.Delete(lower);
        }
    }

    private static FileRenamePlan? FindPlanForSource(IReadOnlyList<FileRenamePlan> plans, string sourceFile)
    {
        foreach (var plan in plans)
        {
            if (ReferToSameFile(plan.SourceFile, sourceFile))
                return plan;
        }

        return null;
    }

    private sealed class FileRenamePlan
    {
        public FileRenamePlan(string sourceFile, string destinationFile, string typeName, Document document)
        {
            SourceFile = sourceFile;
            DestinationFile = destinationFile;
            TypeName = typeName;
            Documents.Add(document);
        }

        public string SourceFile { get; }
        public string DestinationFile { get; }
        public string TypeName { get; }
        public List<Document> Documents { get; } = [];

        public void AddDocument(Document document)
        {
            if (Documents.Any(d => d.Id == document.Id))
                return;
            Documents.Add(document);
        }
    }

    private static Contracts.Models.SymbolInfo CreateSymbolInfo(
        ISymbol? symbol,
        string typeName,
        SyntaxNode declaration,
        string previousFile,
        string newFile)
    {
        var lineSpan = declaration.GetLocation().GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;

        return new Contracts.Models.SymbolInfo
        {
            Name = typeName,
            FullyQualifiedName = symbol?.ToDisplayString() ?? typeName,
            Kind = symbol != null ? MapSymbolKind(symbol) : MapSyntaxKind(declaration),
            PreviousLocation = new SymbolLocation
            {
                File = previousFile,
                Line = line,
                Column = column
            },
            NewLocation = new SymbolLocation
            {
                File = newFile,
                Line = line,
                Column = column
            }
        };
    }

    private static Contracts.Enums.SymbolKind MapSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => namedType.IsRecord
                    ? Contracts.Enums.SymbolKind.Record
                    : Contracts.Enums.SymbolKind.Class,
                TypeKind.Struct => namedType.IsRecord
                    ? Contracts.Enums.SymbolKind.Record
                    : Contracts.Enums.SymbolKind.Struct,
                TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
                TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
                TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
                _ => Contracts.Enums.SymbolKind.Class
            },
            _ => Contracts.Enums.SymbolKind.Class
        };
    }

    private static Contracts.Enums.SymbolKind MapSyntaxKind(SyntaxNode node) => node switch
    {
        InterfaceDeclarationSyntax => Contracts.Enums.SymbolKind.Interface,
        StructDeclarationSyntax => Contracts.Enums.SymbolKind.Struct,
        RecordDeclarationSyntax => Contracts.Enums.SymbolKind.Record,
        EnumDeclarationSyntax => Contracts.Enums.SymbolKind.Enum,
        DelegateDeclarationSyntax => Contracts.Enums.SymbolKind.Delegate,
        _ => Contracts.Enums.SymbolKind.Class
    };
}

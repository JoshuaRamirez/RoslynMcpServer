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

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RenameFileToMatchTypeParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var types = FindTopLevelTypes(root);
        var selected = ResolvePrimaryType(types, @params);
        var typeName = selected.Name;
        var newFilePath = GetTargetFilePath(@params.SourceFile, typeName);

        if (FileNameMatchesType(@params.SourceFile, typeName))
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                $"File already matches type '{typeName}'.");
        }

        if (IsDestinationOccupiedByDifferentFile(@params.SourceFile, newFilePath))
        {
            throw new RefactoringException(
                ErrorCodes.TargetFileExists,
                $"Destination file already exists: {newFilePath}");
        }

        var symbol = semanticModel.GetDeclaredSymbol(selected.Node, cancellationToken);
        var projectPath = document.Project.FilePath;
        var updatedProjectText = TryGetUpdatedProjectText(projectPath, @params.SourceFile, newFilePath);

        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params.SourceFile,
                newFilePath,
                typeName,
                updatedProjectText != null ? projectPath : null);
        }

        try
        {
            MoveSourceFile(@params.SourceFile, newFilePath);
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
                FilesDeleted = [@params.SourceFile]
            },
            CreateSymbolInfo(symbol, typeName, selected.Node, @params.SourceFile, newFilePath),
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

        if (@params.Line.HasValue)
        {
            var atLocation = candidates
                .Where(t => SpanCoversLine(t.Node.GetLocation().GetLineSpan(), @params.Line.Value, @params.Column))
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
        if (line == endLine && column > endCol)
            return false;
        return true;
    }

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

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string sourceFile,
        string newFilePath,
        string typeName,
        string? projectPath)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = sourceFile,
                ChangeType = ChangeKind.Delete,
                Description = $"Rename file to match type '{typeName}'"
            },
            new()
            {
                File = newFilePath,
                ChangeType = ChangeKind.Create,
                Description = $"Rename '{Path.GetFileName(sourceFile)}' to '{Path.GetFileName(newFilePath)}'"
            }
        };

        if (!string.IsNullOrWhiteSpace(projectPath))
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

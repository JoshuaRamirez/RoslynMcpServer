using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Renames any symbol with automatic reference updates across the solution.
/// </summary>
public sealed class RenameSymbolOperation : RefactoringOperationBase<RenameSymbolParams>
{
    private static readonly Regex IdentifierPattern = new(
        @"^@?[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Creates a new rename symbol operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public RenameSymbolOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RenameSymbolParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.NewName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IdentifierPattern.IsMatch(@params.NewName))
            throw new RefactoringException(ErrorCodes.InvalidNewName, $"'{@params.NewName}' is not a valid C# identifier.");

        if (SyntaxFacts.GetKeywordKind(@params.NewName) != SyntaxKind.None)
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"'{@params.NewName}' is a C# reserved keyword.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.SymbolName == @params.NewName)
            throw new RefactoringException(ErrorCodes.SameLocation, "New name is the same as current name.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RenameSymbolParams @params,
        CancellationToken cancellationToken)
    {
        // Find the symbol
        var (symbol, document) = await FindSymbolAsync(@params, cancellationToken);

        // Validate rename is allowed
        ValidateRename(symbol, @params);

        // Find all references before rename
        var references = await ReferenceTracker.FindAllReferencesAsync(symbol, cancellationToken);

        // Compute rename options
        var options = new SymbolRenameOptions(
            RenameOverloads: @params.RenameOverloads,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false // We handle file rename separately
        );

        // Perform the rename
        var newSolution = await Renamer.RenameSymbolAsync(
            Context.Solution,
            symbol,
            options,
            @params.NewName,
            cancellationToken);

        // Handle file rename for types
        string? renamedFile = null;
        if (@params.RenameFile && symbol is INamedTypeSymbol namedType && document.FilePath != null)
        {
            var fileName = Path.GetFileNameWithoutExtension(document.FilePath);
            if (fileName == symbol.Name)
            {
                var newFileName = @params.NewName + ".cs";
                var newFilePath = Path.Combine(Path.GetDirectoryName(document.FilePath)!, newFileName);

                // Rename the document in the solution
                var doc = newSolution.GetDocument(document.Id);
                if (doc != null)
                {
                    newSolution = newSolution.WithDocumentFilePath(document.Id, newFilePath);
                    renamedFile = newFilePath;
                }
            }
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, symbol, @params, references.TotalReferenceCount, renamedFile);
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        // Handle physical file rename
        string? fileRenameWarning = null;
        bool fileRenameSucceeded = false;
        if (renamedFile != null && document.FilePath != null)
        {
            try
            {
                if (File.Exists(document.FilePath) && !File.Exists(renamedFile))
                {
                    File.Move(document.FilePath, renamedFile);
                    fileRenameSucceeded = true;
                }
                else if (File.Exists(renamedFile))
                {
                    fileRenameWarning = $"File rename skipped: target file '{renamedFile}' already exists.";
                }
            }
            catch (IOException ex)
            {
                fileRenameWarning = $"File rename failed: {ex.Message}. Code references were updated but file was not renamed.";
                renamedFile = null; // Clear to indicate file was not actually renamed
            }
        }

        var changes = new FileChanges
        {
            FilesModified = commitResult.FilesModified,
            FilesCreated = fileRenameSucceeded ? commitResult.FilesCreated.Concat(new[] { renamedFile! }).ToList() : commitResult.FilesCreated,
            FilesDeleted = fileRenameSucceeded ? commitResult.FilesDeleted.Concat(new[] { document.FilePath! }).ToList() : commitResult.FilesDeleted
        };

        var result = RefactoringResult.Succeeded(
            operationId,
            changes,
            CreateSymbolInfo(symbol, @params.NewName, document.FilePath, fileRenameSucceeded ? renamedFile : null),
            references.TotalReferenceCount,
            0);

        // Include warning in result if file rename failed
        if (fileRenameWarning != null)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = result.OperationId,
                Preview = result.Preview,
                Changes = result.Changes,
                Symbol = result.Symbol,
                ReferencesUpdated = result.ReferencesUpdated,
                UsingDirectivesAdded = result.UsingDirectivesAdded,
                UsingDirectivesRemoved = result.UsingDirectivesRemoved,
                ExecutionTimeMs = result.ExecutionTimeMs,
                Error = RefactoringError.Create("PARTIAL_SUCCESS", fileRenameWarning),
                PendingChanges = result.PendingChanges
            };
        }

        return result;
    }

    private async Task<(ISymbol Symbol, Document Document)> FindSymbolAsync(
        RenameSymbolParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // If line/column provided, find symbol at position
        if (@params.Line.HasValue)
        {
            var position = GetPosition(root, @params.Line.Value, @params.Column ?? 1);
            var token = root.FindToken(position);

            // Walk up to find the symbol declaration or reference
            var node = token.Parent;
            while (node != null)
            {
                var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                if (symbol != null && symbol.Name == @params.SymbolName)
                {
                    return (symbol, document);
                }

                // Check for symbol info (reference)
                var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
                if (symbolInfo.Symbol != null && symbolInfo.Symbol.Name == @params.SymbolName)
                {
                    return (symbolInfo.Symbol, document);
                }

                node = node.Parent;
            }

            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{@params.SymbolName}' found at line {@params.Line}.");
        }

        // Otherwise, search by name
        var candidates = new List<ISymbol>();

        foreach (var node in root.DescendantNodes())
        {
            var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (symbol != null && symbol.Name == @params.SymbolName)
            {
                candidates.Add(symbol);
            }
        }

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{@params.SymbolName}' found in file.");
        }

        if (candidates.Count > 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple symbols named '{@params.SymbolName}' found. Provide line number to disambiguate.",
                new Dictionary<string, object>
                {
                    ["candidateCount"] = candidates.Count
                });
        }

        return (candidates[0], document);
    }

    /// <summary>
    /// Converts 1-based line/column to absolute position with bounds validation.
    /// </summary>
    /// <param name="root">Syntax root to get text from.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <returns>Absolute position in text.</returns>
    /// <exception cref="RefactoringException">Thrown if line/column is out of bounds.</exception>
    private static int GetPosition(SyntaxNode root, int line, int column)
    {
        var text = root.GetText();
        var lineIndex = line - 1; // Convert to 0-based

        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidLineNumber,
                $"Line {line} is out of range. File has {text.Lines.Count} lines.");
        }

        var lineInfo = text.Lines[lineIndex];
        var columnIndex = column - 1; // Convert to 0-based
        var lineLength = lineInfo.End - lineInfo.Start;

        if (columnIndex < 0 || columnIndex > lineLength)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidColumnNumber,
                $"Column {column} is out of range for line {line} (line has {lineLength} characters).");
        }

        return lineInfo.Start + columnIndex;
    }

    private static void ValidateRename(ISymbol symbol, RenameSymbolParams @params)
    {
        // Cannot rename constructors directly
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.Constructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameConstructor,
                    "Cannot rename constructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.Destructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameDestructor,
                    "Cannot rename destructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.UserDefinedOperator ||
                method.MethodKind == MethodKind.Conversion)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameOperator,
                    "Cannot rename operators.");
            }
        }

        // Cannot rename symbols from external assemblies
        if (symbol.ContainingAssembly != null &&
            !symbol.Locations.Any(l => l.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.CannotRenameExternal,
                "Cannot rename symbols from external assemblies.");
        }
    }

    /// <summary>
    /// Creates symbol information for the result, with safe null handling for locations.
    /// </summary>
    private static Contracts.Models.SymbolInfo CreateSymbolInfo(
        ISymbol symbol,
        string newName,
        string? previousFile,
        string? newFile)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        FileLinePositionSpan? lineSpan = null;

        // Safely get line span only if location is valid and in source
        if (location != null && location.IsInSource)
        {
            try
            {
                var span = location.GetLineSpan();
                // Validate the span has meaningful data
                if (span.Path != null || span.StartLinePosition.Line >= 0)
                {
                    lineSpan = span;
                }
            }
            catch (InvalidOperationException)
            {
                // GetLineSpan can throw if location is invalid - treat as no location
            }
        }

        SymbolLocation? prevLocation = null;
        SymbolLocation? newLocation = null;

        if (previousFile != null && lineSpan.HasValue)
        {
            prevLocation = new SymbolLocation
            {
                File = previousFile,
                Line = lineSpan.Value.StartLinePosition.Line + 1,
                Column = lineSpan.Value.StartLinePosition.Character + 1
            };
        }

        if (newFile != null)
        {
            // Use line span if available, otherwise default to line 1, column 1
            newLocation = new SymbolLocation
            {
                File = newFile,
                Line = lineSpan?.StartLinePosition.Line + 1 ?? 1,
                Column = lineSpan?.StartLinePosition.Character + 1 ?? 1
            };
        }

        return new Contracts.Models.SymbolInfo
        {
            Name = newName,
            FullyQualifiedName = symbol.ToDisplayString().Replace(symbol.Name, newName),
            Kind = MapSymbolKind(symbol),
            PreviousLocation = prevLocation,
            NewLocation = newLocation
        };
    }

    private static Contracts.Enums.SymbolKind MapSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => Contracts.Enums.SymbolKind.Class,
                TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
                TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
                TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
                TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
                _ when namedType.IsRecord => Contracts.Enums.SymbolKind.Record,
                _ => Contracts.Enums.SymbolKind.Class
            },
            IMethodSymbol => Contracts.Enums.SymbolKind.Method,
            IPropertySymbol => Contracts.Enums.SymbolKind.Property,
            IFieldSymbol => Contracts.Enums.SymbolKind.Field,
            IEventSymbol => Contracts.Enums.SymbolKind.Event,
            ILocalSymbol => Contracts.Enums.SymbolKind.Local,
            IParameterSymbol => Contracts.Enums.SymbolKind.Parameter,
            INamespaceSymbol => Contracts.Enums.SymbolKind.Namespace,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ISymbol symbol,
        RenameSymbolParams @params,
        int referenceCount,
        string? renamedFile)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Rename '{symbol.Name}' to '{@params.NewName}'"
            }
        };

        if (referenceCount > 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = "(multiple files)",
                ChangeType = ChangeKind.Modify,
                Description = $"Update {referenceCount} reference(s)"
            });
        }

        if (renamedFile != null)
        {
            pendingChanges.Add(new PendingChange
            {
                File = renamedFile,
                ChangeType = ChangeKind.Create,
                Description = "Rename file to match type name"
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

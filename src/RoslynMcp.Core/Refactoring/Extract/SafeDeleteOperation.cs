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
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Deletes a selected symbol only when it has no remaining references.
/// Remaining usages are reported as an error with locations; no force-delete
/// or reference cleanup is performed.
/// </summary>
public sealed class SafeDeleteOperation : RefactoringOperationBase<SafeDeleteParams>
{
    /// <summary>
    /// Creates a new safe-delete operation.
    /// </summary>
    public SafeDeleteOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(SafeDeleteParams @params) => Validate(@params);

    /// <summary>
    /// Validates safe-delete parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(SafeDeleteParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.StartColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (@params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.EndColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (@params.EndLine < @params.StartLine ||
            (@params.EndLine == @params.StartLine && @params.EndColumn < @params.StartColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <summary>
    /// Rejects documents that cannot receive source edits.
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

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        SafeDeleteParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = GetSelectionSpan(sourceText, @params);
        var symbol = ResolveSelectedSymbol(
            root, semanticModel, span, @params, cancellationToken);

        symbol = NormalizeDeletableSymbol(symbol);
        ValidateSymbolCanBeDeleted(symbol);

        var declarationDocuments = await GetDeclarationDocumentsAsync(symbol, cancellationToken);
        foreach (var declarationDocument in declarationDocuments)
            ValidateDocumentIsEditable(declarationDocument, Context.Workspace);

        var usages = await FindUsagesAsync(symbol, cancellationToken);
        if (!CanSafelyDelete(usages))
            throw CreateHasUsagesException(symbol, usages);

        var plan = await BuildPlanAsync(symbol, cancellationToken);
        if (@params.Preview)
            return CreatePreviewResult(operationId, plan);

        var newSolution = await ApplyPlanAsync(Context.Solution, plan, cancellationToken);
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
                Name = symbol.Name,
                FullyQualifiedName = symbol.ToDisplayString(),
                Kind = SymbolKindMapper.Map(symbol)
            },
            0,
            0);
    }

    internal static TextSpan GetSelectionSpan(SourceText sourceText, SafeDeleteParams @params)
    {
        if (@params.StartLine > sourceText.Lines.Count || @params.EndLine > sourceText.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Selection is outside the file.");

        var startLine = sourceText.Lines[@params.StartLine - 1];
        var endLine = sourceText.Lines[@params.EndLine - 1];
        if (@params.StartColumn - 1 > startLine.Span.Length || @params.EndColumn - 1 > endLine.SpanIncludingLineBreak.Length)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Selection column is outside the line.");

        var startPosition = startLine.Start + @params.StartColumn - 1;
        var endPosition = endLine.Start + @params.EndColumn - 1;
        if (endPosition < startPosition)
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    private static ISymbol ResolveSelectedSymbol(
        SyntaxNode root,
        SemanticModel semanticModel,
        TextSpan span,
        SafeDeleteParams @params,
        CancellationToken cancellationToken)
    {
        var token = root.FindToken(span.Start);
        if (token.Span.OverlapsWith(span) || span.OverlapsWith(token.Span))
        {
            var tokenNode = token.Parent;
            if (tokenNode != null)
            {
                var declaredOnToken = semanticModel.GetDeclaredSymbol(tokenNode, cancellationToken);
                if (declaredOnToken != null && IdentifierOverlaps(tokenNode, span))
                    return ConfirmSymbolName(declaredOnToken, @params.SymbolName);

                if (token.IsKind(SyntaxKind.IdentifierToken))
                {
                    var tokenSymbol = semanticModel.GetSymbolInfo(tokenNode, cancellationToken).Symbol;
                    if (tokenSymbol != null)
                        return ConfirmSymbolName(tokenSymbol, @params.SymbolName);
                }
            }
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var declared = semanticModel.GetDeclaredSymbol(node, cancellationToken);
        if (declared != null && IdentifierOverlaps(node, span))
            return ConfirmSymbolName(declared, @params.SymbolName);

        throw new RefactoringException(
            ErrorCodes.SymbolNotFound,
            "No symbol found at the specified selection.");
    }

    private static ISymbol ConfirmSymbolName(ISymbol symbol, string? expectedName)
    {
        if (!string.IsNullOrWhiteSpace(expectedName) && symbol.Name != expectedName)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{expectedName}' found at the specified selection.");
        }

        return symbol;
    }

    private static bool IdentifierOverlaps(SyntaxNode node, TextSpan span)
    {
        var identifier = GetDeclarationIdentifier(node);
        if (identifier != null)
            return identifier.Value.Span.OverlapsWith(span) || span.OverlapsWith(identifier.Value.Span);

        return node is MemberDeclarationSyntax && node.Span.OverlapsWith(span);
    }

    private static SyntaxToken? GetDeclarationIdentifier(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        VariableDeclaratorSyntax variable => variable.Identifier,
        TypeDeclarationSyntax type => type.Identifier,
        EnumDeclarationSyntax @enum => @enum.Identifier,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier,
        EnumMemberDeclarationSyntax member => member.Identifier,
        ParameterSyntax parameter => parameter.Identifier,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        _ => null
    };

    private static ISymbol NormalizeDeletableSymbol(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;

        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated } &&
            associated.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or Microsoft.CodeAnalysis.SymbolKind.Event)
        {
            return associated.OriginalDefinition;
        }

        return symbol;
    }

    private static void ValidateSymbolCanBeDeleted(ISymbol symbol)
    {
        if (symbol.Kind is Microsoft.CodeAnalysis.SymbolKind.Namespace
            or Microsoft.CodeAnalysis.SymbolKind.NetModule
            or Microsoft.CodeAnalysis.SymbolKind.Assembly)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{symbol.Name}' cannot be safe-deleted.");
        }

        if (symbol is IParameterSymbol or ITypeParameterSymbol)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{symbol.Name}' cannot be safe-deleted without changing a signature.");
        }

        if (!symbol.Locations.Any(location => location.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Symbol '{symbol.Name}' is not in an editable document.");
        }
    }

    private async Task<IReadOnlyList<Document>> GetDeclarationDocumentsAsync(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var documents = new List<Document>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var document = Context.Solution.GetDocument(syntax.SyntaxTree);
            if (document == null)
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Declaration of '{symbol.Name}' is not in an editable document.");
            }

            documents.Add(document);
        }

        if (documents.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Symbol '{symbol.Name}' is not in an editable document.");
        }

        return documents;
    }

    private async Task<IReadOnlyList<UsageLocation>> FindUsagesAsync(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, Context.Solution, cancellationToken);
        var usages = new List<UsageLocation>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Document == null || !location.Location.IsInSource)
                    continue;

                if (IsDefinitionLocation(referencedSymbol.Definition, location.Location))
                    continue;

                var lineSpan = location.Location.GetLineSpan();
                usages.Add(new UsageLocation(
                    location.Document.FilePath ?? lineSpan.Path,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    GetSnippet(location.Location)));
            }
        }

        return usages;
    }

    private static bool CanSafelyDelete(IReadOnlyList<UsageLocation> usages) => usages.Count == 0;

    private static bool IsDefinitionLocation(ISymbol symbol, Location location)
    {
        return symbol.Locations.Any(definition =>
            definition.IsInSource &&
            definition.SourceTree == location.SourceTree &&
            definition.SourceSpan == location.SourceSpan);
    }

    private static string? GetSnippet(Location location)
    {
        if (location.SourceTree == null)
            return null;

        var text = location.SourceTree.GetText();
        var line = location.GetLineSpan().StartLinePosition.Line;
        if (line < 0 || line >= text.Lines.Count)
            return null;

        return text.Lines[line].ToString().Trim();
    }

    private static RefactoringException CreateHasUsagesException(
        ISymbol symbol,
        IReadOnlyList<UsageLocation> usages)
    {
        var locations = usages
            .Select(usage => new Dictionary<string, object>
            {
                ["file"] = usage.File,
                ["line"] = usage.Line,
                ["column"] = usage.Column,
                ["snippet"] = usage.Snippet ?? string.Empty
            })
            .ToList();

        return new RefactoringException(
            ErrorCodes.MemberHasUsages,
            $"Cannot safely delete '{symbol.Name}' because it has {usages.Count} remaining reference(s).",
            new Dictionary<string, object>
            {
                ["symbolName"] = symbol.Name,
                ["usageCount"] = usages.Count,
                ["locations"] = locations
            },
            ["Remove or update the remaining references, then retry."]);
    }

    private static async Task<DeletePlan> BuildPlanAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        var removals = new List<NodeRemoval>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var removable = GetRemovableNode(syntax);
            removals.Add(new NodeRemoval(removable.SyntaxTree, removable.Span, removable.ToFullString().Trim()));
        }

        if (removals.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate a declaration to delete for '{symbol.Name}'.");
        }

        return new DeletePlan(symbol.Name, symbol.ToDisplayString(), SymbolKindMapper.Map(symbol), removals);
    }

    private static SyntaxNode GetRemovableNode(SyntaxNode declaration)
    {
        if (declaration is VariableDeclaratorSyntax declarator)
        {
            if (declarator.Parent is VariableDeclarationSyntax { Variables.Count: 1 } variableDeclaration)
            {
                if (variableDeclaration.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax
                    or LocalDeclarationStatementSyntax)
                {
                    return variableDeclaration.Parent;
                }
            }

            return declarator;
        }

        if (declaration is MemberDeclarationSyntax member)
            return member;

        if (declaration is LocalFunctionStatementSyntax localFunction)
            return localFunction;

        return declaration.FirstAncestorOrSelf<MemberDeclarationSyntax>()
            ?? declaration.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>()
            ?? declaration;
    }

    private static async Task<Solution> ApplyPlanAsync(
        Solution solution,
        DeletePlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var group in plan.Removals.GroupBy(removal => removal.SyntaxTree))
        {
            var document = solution.GetDocument(group.Key)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    "A declaration document is no longer in the workspace.");

            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var nodes = group
                .Select(removal => root.DescendantNodesAndSelf()
                    .FirstOrDefault(node => node.Span == removal.Span)
                    ?? GetRemovableNode(root.FindNode(removal.Span, getInnermostNodeForTie: true)))
                .Distinct()
                .ToList();

            var newRoot = root.RemoveNodes(nodes, SyntaxRemoveOptions.KeepNoTrivia)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Deleting the symbol produced an empty document root.");

            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        return solution;
    }

    private static RefactoringResult CreatePreviewResult(Guid operationId, DeletePlan plan)
    {
        var pendingChanges = plan.Removals
            .Select(removal => new PendingChange
            {
                File = removal.SyntaxTree.FilePath ?? string.Empty,
                ChangeType = ChangeKind.Modify,
                Description = $"Delete unused {plan.Kind.ToString().ToLowerInvariant()} '{plan.Name}'",
                BeforeSnippet = removal.Text,
                AfterSnippet = "// (removed)"
            })
            .ToList();

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record UsageLocation(string File, int Line, int Column, string? Snippet);

    private sealed record NodeRemoval(SyntaxTree SyntaxTree, TextSpan Span, string Text);

    private sealed record DeletePlan(
        string Name,
        string FullyQualifiedName,
        Contracts.Enums.SymbolKind Kind,
        IReadOnlyList<NodeRemoval> Removals);
}

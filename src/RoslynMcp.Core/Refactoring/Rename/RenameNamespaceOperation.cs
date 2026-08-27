using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Renames a namespace across the solution, updating declarations,
/// using directives, and qualified name references.
/// </summary>
public sealed class RenameNamespaceOperation : RefactoringOperationBase<RenameNamespaceParams>
{
    private static readonly Regex NamespacePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Creates a new rename-namespace operation.
    /// </summary>
    public RenameNamespaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RenameNamespaceParams @params) => Validate(@params);

    /// <summary>
    /// Validates rename-namespace inputs. Internal so tests can exercise
    /// rules without loading a workspace.
    /// </summary>
    internal static void Validate(RenameNamespaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.NamespaceName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "namespaceName is required.");

        if (string.IsNullOrWhiteSpace(@params.NewName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!IsValidNamespaceName(@params.NamespaceName))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidNamespace,
                $"'{@params.NamespaceName}' is not a valid namespace name.");
        }

        if (!IsValidNamespaceName(@params.NewName))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidNamespace,
                $"'{@params.NewName}' is not a valid namespace name.");
        }

        foreach (var segment in SplitNamespace(@params.NewName))
        {
            if (SyntaxFacts.GetKeywordKind(segment) != SyntaxKind.None)
            {
                throw new RefactoringException(
                    ErrorCodes.ReservedKeyword,
                    $"'{segment}' is a C# reserved keyword.");
            }
        }

        if (string.Equals(@params.NamespaceName.Trim(), @params.NewName.Trim(), StringComparison.Ordinal))
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                "New name is the same as current name.");
        }

        if (@params.UpdateFolders)
        {
            throw new RefactoringException(
                ErrorCodes.FolderUpdateNotSupported,
                "updateFolders is not implemented. Leave updateFolders false to rename the namespace without moving folders.");
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> is a dotted sequence of C# identifiers.
    /// </summary>
    internal static bool IsValidNamespaceName(string name) =>
        !string.IsNullOrWhiteSpace(name) && NamespacePattern.IsMatch(name.Trim());

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
        RenameNamespaceParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var namespaceSymbol = await FindNamespaceAsync(document, @params, cancellationToken);
        var oldFullName = GetFullName(namespaceSymbol);
        var newFullName = @params.NewName.Trim();

        if (string.Equals(oldFullName, newFullName, StringComparison.Ordinal))
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                "New name is the same as current name.");
        }

        var compilation = await document.Project.GetCompilationAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not get compilation.");

        ValidateNoNameConflict(namespaceSymbol, newFullName, compilation);

        var references = await ReferenceTracker.FindAllReferencesAsync(namespaceSymbol, cancellationToken);
        var newSolution = await ComputeRenamedSolutionAsync(
            namespaceSymbol,
            oldFullName,
            newFullName,
            cancellationToken);

        ValidateChangedDocumentsAreEditable(Context.Solution, newSolution);

        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, Context.Solution, newSolution, oldFullName, newFullName);
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            CreateSymbolInfo(namespaceSymbol, oldFullName, newFullName, document.FilePath),
            references.TotalReferenceCount,
            0);
    }

    /// <summary>
    /// Resolves the namespace declared in <paramref name="document"/> that matches
    /// <see cref="RenameNamespaceParams.NamespaceName"/>.
    /// </summary>
    internal static async Task<INamespaceSymbol> FindNamespaceAsync(
        Document document,
        RenameNamespaceParams @params,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var requested = @params.NamespaceName.Trim();
        var declarations = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToList();
        var matches = new List<(INamespaceSymbol Symbol, BaseNamespaceDeclarationSyntax Declaration)>();

        foreach (var declaration in declarations)
        {
            if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamespaceSymbol declared)
                continue;

            foreach (var candidate in EnumerateNamespaceChain(declared))
            {
                if (NamespaceMatches(candidate, declaration, requested, declared))
                    matches.Add((candidate, declaration));
            }
        }

        matches = matches
            .DistinctBy(m => (GetFullName(m.Symbol), m.Declaration.SpanStart))
            .ToList();

        if (@params.Line.HasValue)
        {
            var atLocation = matches
                .Where(m => SpanCoversLine(m.Declaration.GetLocation().GetLineSpan(), @params.Line.Value, @params.Column))
                .ToList();

            if (atLocation.Count == 1)
                return atLocation[0].Symbol;

            if (atLocation.Count == 0)
            {
                throw new RefactoringException(
                    ErrorCodes.SymbolNotFound,
                    $"No namespace named '{requested}' found at line {@params.Line}.");
            }

            var distinct = atLocation.Select(m => GetFullName(m.Symbol)).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 1)
                return atLocation[0].Symbol;

            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple namespaces named '{requested}' found at line {@params.Line}. Provide column.");
        }

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No namespace named '{requested}' found in file.");
        }

        var unique = matches.Select(m => m.Symbol).DistinctBy(GetFullName).ToList();
        if (unique.Count == 1)
            return unique[0];

        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple namespaces named '{requested}' found. Provide line number to disambiguate.",
            new Dictionary<string, object>
            {
                ["candidateCount"] = unique.Count
            });
    }

    /// <summary>
    /// True when the declared or containing namespace matches <paramref name="requested"/>.
    /// </summary>
    internal static bool NamespaceMatches(
        INamespaceSymbol symbol,
        BaseNamespaceDeclarationSyntax declaration,
        string requested,
        INamespaceSymbol declaredSymbol)
    {
        if (symbol.IsGlobalNamespace)
            return false;

        if (string.Equals(GetFullName(symbol), requested, StringComparison.Ordinal))
            return true;

        if (string.Equals(symbol.Name, requested, StringComparison.Ordinal))
            return true;

        return SymbolEqualityComparer.Default.Equals(symbol, declaredSymbol)
               && string.Equals(declaration.Name.ToString(), requested, StringComparison.Ordinal);
    }

    /// <summary>
    /// Display name of a namespace, empty for the global namespace.
    /// </summary>
    internal static string GetFullName(INamespaceSymbol symbol) =>
        symbol.IsGlobalNamespace ? string.Empty : symbol.ToDisplayString();

    /// <summary>
    /// Splits a dotted namespace into identifier segments.
    /// </summary>
    internal static string[] SplitNamespace(string name) =>
        name.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parent path of a dotted namespace, or empty when it is a single segment.
    /// </summary>
    internal static string GetParentName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot < 0 ? string.Empty : fullName[..lastDot];
    }

    /// <summary>
    /// Last identifier of a dotted namespace.
    /// </summary>
    internal static string GetLastSegment(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot < 0 ? fullName : fullName[(lastDot + 1)..];
    }

    /// <summary>
    /// True when <paramref name="oldFullName"/> and <paramref name="newFullName"/> share
    /// a parent path so only the last identifier needs to change.
    /// </summary>
    internal static bool IsLastSegmentRename(string oldFullName, string newFullName) =>
        string.Equals(GetParentName(oldFullName), GetParentName(newFullName), StringComparison.Ordinal)
        && !string.Equals(GetLastSegment(oldFullName), GetLastSegment(newFullName), StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="prefix"/> is a proper dotted prefix of <paramref name="fullName"/>.
    /// </summary>
    internal static bool IsPrefixOfNamespace(string prefix, string fullName) =>
        !string.IsNullOrEmpty(prefix)
        && fullName.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>
    /// Declaration name to write inside <paramref name="enclosingFullName"/> so the
    /// resulting fully qualified name is <paramref name="fullName"/>.
    /// </summary>
    internal static string GetRelativeNamespaceName(string fullName, string enclosingFullName)
    {
        if (string.IsNullOrEmpty(enclosingFullName))
            return fullName;

        if (IsPrefixOfNamespace(enclosingFullName, fullName))
            return fullName[(enclosingFullName.Length + 1)..];

        if (string.Equals(fullName, enclosingFullName, StringComparison.Ordinal))
            return GetLastSegment(fullName);

        return fullName;
    }

    /// <summary>
    /// Maps an ancestor declaration of <paramref name="oldFullName"/> onto the
    /// corresponding prefix of <paramref name="newFullName"/>.
    /// </summary>
    internal static string GetCorrespondingPrefix(string oldFullName, string newFullName, string declaredFullName)
    {
        if (string.Equals(declaredFullName, oldFullName, StringComparison.Ordinal)
            || IsPrefixOfNamespace(oldFullName, declaredFullName))
        {
            return RewriteFullName(declaredFullName, oldFullName, newFullName);
        }

        if (!IsPrefixOfNamespace(declaredFullName, oldFullName))
            return declaredFullName;

        var declaredSegments = SplitNamespace(declaredFullName);
        var newSegments = SplitNamespace(newFullName);
        if (declaredSegments.Length >= newSegments.Length)
            return newFullName;

        return string.Join(".", newSegments.Take(declaredSegments.Length));
    }

    /// <summary>
    /// Replaces <paramref name="oldFullName"/> (or that prefix) in <paramref name="current"/>
    /// with <paramref name="newFullName"/>.
    /// </summary>
    internal static string RewriteFullName(string current, string oldFullName, string newFullName)
    {
        if (string.Equals(current, oldFullName, StringComparison.Ordinal))
            return newFullName;

        if (IsPrefixOfNamespace(oldFullName, current))
            return newFullName + current[oldFullName.Length..];

        return current;
    }

    internal static bool SpanCoversLine(FileLinePositionSpan span, int line, int? column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        if (line < startLine || line > endLine)
            return false;

        if (!column.HasValue)
            return true;

        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;
        if (line == startLine && column.Value < startCol)
            return false;
        if (line == endLine && column.Value > endCol)
            return false;
        return true;
    }

    private static IEnumerable<INamespaceSymbol> EnumerateNamespaceChain(INamespaceSymbol symbol)
    {
        for (var current = symbol; current is { IsGlobalNamespace: false }; current = current.ContainingNamespace)
            yield return current;
    }

    private static void ValidateNoNameConflict(
        INamespaceSymbol source,
        string newFullName,
        Compilation compilation)
    {
        var targetParentName = GetParentName(newFullName);
        var targetLast = GetLastSegment(newFullName);
        var targetParent = FindNamespace(compilation.GlobalNamespace, targetParentName);
        if (targetParent == null)
            return;

        foreach (var member in targetParent.GetMembers(targetLast))
        {
            if (SymbolEqualityComparer.Default.Equals(member, source))
                continue;

            if (member is INamedTypeSymbol type)
            {
                throw new RefactoringException(
                    ErrorCodes.NameConflictScope,
                    $"New namespace name '{newFullName}' conflicts with type '{type.ToDisplayString()}'.",
                    suggestions: ["Choose a different new name"]);
            }

            if (member is INamespaceSymbol existing && HasNamespaceCollision(source, existing))
            {
                throw new RefactoringException(
                    ErrorCodes.NameConflictScope,
                    $"Namespace '{newFullName}' already contains a type or namespace that would collide with '{GetFullName(source)}'.",
                    suggestions: ["Rename the conflicting type first", "Choose a different new name"]);
            }
        }
    }

    /// <summary>
    /// True when merging <paramref name="source"/> into <paramref name="target"/> would
    /// create duplicate types or namespace/type name clashes, including descendants.
    /// </summary>
    internal static bool HasNamespaceCollision(INamespaceSymbol source, INamespaceSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(source, target))
            return false;

        var targetTypeNames = target.GetTypeMembers().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var targetNamespaceNames = target.GetNamespaceMembers().Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

        if (source.GetTypeMembers().Any(t =>
                targetTypeNames.Contains(t.Name) || targetNamespaceNames.Contains(t.Name)))
        {
            return true;
        }

        if (target.GetTypeMembers().Any(t =>
                source.GetNamespaceMembers().Any(n => string.Equals(n.Name, t.Name, StringComparison.Ordinal))))
        {
            return true;
        }

        foreach (var sourceChild in source.GetNamespaceMembers())
        {
            var targetChild = target.GetNamespaceMembers()
                .FirstOrDefault(n => string.Equals(n.Name, sourceChild.Name, StringComparison.Ordinal));
            if (targetChild != null && HasNamespaceCollision(sourceChild, targetChild))
                return true;
        }

        return false;
    }

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol current, string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return current;

        foreach (var segment in SplitNamespace(fullName))
        {
            var next = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == segment);
            if (next == null)
                return null;
            current = next;
        }

        return current;
    }

    private async Task<Solution> ComputeRenamedSolutionAsync(
        INamespaceSymbol namespaceSymbol,
        string oldFullName,
        string newFullName,
        CancellationToken cancellationToken)
    {
        if (IsLastSegmentRename(oldFullName, newFullName))
        {
            var options = new SymbolRenameOptions(
                RenameOverloads: false,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false);

            return await Renamer.RenameSymbolAsync(
                Context.Solution,
                namespaceSymbol,
                options,
                GetLastSegment(newFullName),
                cancellationToken);
        }

        return await RewriteFullNamespaceAsync(namespaceSymbol, oldFullName, newFullName, cancellationToken);
    }

    /// <summary>
    /// Rewrites namespace declarations and references in <paramref name="root"/>.
    /// Exposed for tests that must exercise the rewriter without MSBuild.
    /// </summary>
    internal static SyntaxNode RewriteNamespaceNames(
        SemanticModel semanticModel,
        SyntaxNode root,
        INamespaceSymbol target,
        string oldFullName,
        string newFullName)
    {
        var rewriter = new NamespaceNameRewriter(semanticModel, target, oldFullName, newFullName);
        return rewriter.Visit(root) ?? root;
    }

    private async Task<Solution> RewriteFullNamespaceAsync(
        INamespaceSymbol namespaceSymbol,
        string oldFullName,
        string newFullName,
        CancellationToken cancellationToken)
    {
        var documentIds = await CollectDocumentsToRewriteAsync(namespaceSymbol, cancellationToken);
        // Bind every document against the original compilation. Updating the
        // solution incrementally would produce a new compilation whose
        // INamespaceSymbols no longer equal the resolved source symbol.
        var originalSolution = Context.Solution;
        var updates = new List<(DocumentId Id, SyntaxNode Root)>();

        foreach (var documentId in documentIds)
        {
            var document = originalSolution.GetDocument(documentId);
            if (document == null)
                continue;

            ValidateDocumentIsEditable(document, Context.Workspace);

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || semanticModel == null)
                continue;

            var rewriter = new NamespaceNameRewriter(semanticModel, namespaceSymbol, oldFullName, newFullName);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && newRoot != root)
                updates.Add((documentId, newRoot));
        }

        var solution = originalSolution;
        foreach (var (documentId, newRoot) in updates)
            solution = solution.WithDocumentSyntaxRoot(documentId, newRoot);

        return solution;
    }

    private async Task<HashSet<DocumentId>> CollectDocumentsToRewriteAsync(
        INamespaceSymbol namespaceSymbol,
        CancellationToken cancellationToken)
    {
        var documentIds = new HashSet<DocumentId>();

        foreach (var project in Context.Solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.SupportsSyntaxTree)
                    documentIds.Add(document.Id);
            }
        }

        foreach (var symbol in EnumerateSelfAndDescendants(namespaceSymbol))
        {
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                var document = Context.Solution.GetDocument(syntaxRef.SyntaxTree);
                if (document != null)
                    documentIds.Add(document.Id);
            }
        }

        var references = await SymbolFinder.FindReferencesAsync(
            namespaceSymbol,
            Context.Solution,
            cancellationToken);

        foreach (var referenced in references)
        {
            foreach (var location in referenced.Locations)
            {
                if (location.Document != null)
                    documentIds.Add(location.Document.Id);
            }
        }

        return documentIds;
    }

    private static IEnumerable<INamespaceSymbol> EnumerateSelfAndDescendants(INamespaceSymbol symbol)
    {
        yield return symbol;
        foreach (var child in symbol.GetNamespaceMembers())
        {
            foreach (var descendant in EnumerateSelfAndDescendants(child))
                yield return descendant;
        }
    }

    private void ValidateChangedDocumentsAreEditable(Solution oldSolution, Solution newSolution)
    {
        foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var document = oldSolution.GetDocument(documentId);
                if (document != null)
                    ValidateDocumentIsEditable(document, Context.Workspace);
            }
        }
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        Solution oldSolution,
        Solution newSolution,
        string oldFullName,
        string newFullName)
    {
        var pending = new List<PendingChange>();
        foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var document = oldSolution.GetDocument(documentId);
                pending.Add(new PendingChange
                {
                    File = document?.FilePath ?? document?.Name ?? "(unknown)",
                    ChangeType = ChangeKind.Modify,
                    Description = $"Rename namespace '{oldFullName}' to '{newFullName}'"
                });
            }
        }

        if (pending.Count == 0)
        {
            pending.Add(new PendingChange
            {
                File = "(solution)",
                ChangeType = ChangeKind.Modify,
                Description = $"Rename namespace '{oldFullName}' to '{newFullName}'"
            });
        }

        return RefactoringResult.PreviewResult(operationId, pending);
    }

    private static Contracts.Models.SymbolInfo CreateSymbolInfo(
        INamespaceSymbol symbol,
        string oldFullName,
        string newFullName,
        string? sourceFile)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        FileLinePositionSpan? lineSpan = null;
        if (location is { IsInSource: true })
        {
            try
            {
                lineSpan = location.GetLineSpan();
            }
            catch (InvalidOperationException)
            {
                // Location can be invalid after some workspace states.
            }
        }

        SymbolLocation? previous = null;
        SymbolLocation? next = null;
        if (sourceFile != null && lineSpan.HasValue)
        {
            previous = new SymbolLocation
            {
                File = sourceFile,
                Line = lineSpan.Value.StartLinePosition.Line + 1,
                Column = lineSpan.Value.StartLinePosition.Character + 1
            };
            next = new SymbolLocation
            {
                File = sourceFile,
                Line = previous.Line,
                Column = previous.Column
            };
        }

        return new Contracts.Models.SymbolInfo
        {
            Name = GetLastSegment(newFullName),
            FullyQualifiedName = newFullName,
            Kind = Contracts.Enums.SymbolKind.Namespace,
            PreviousLocation = previous,
            NewLocation = next,
            PreviousNamespace = oldFullName,
            NewNamespace = newFullName
        };
    }

    private sealed class NamespaceNameRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _semanticModel;
        private readonly INamespaceSymbol _target;
        private readonly string _oldFullName;
        private readonly string _newFullName;

        public NamespaceNameRewriter(
            SemanticModel semanticModel,
            INamespaceSymbol target,
            string oldFullName,
            string newFullName)
        {
            _semanticModel = semanticModel;
            _target = target;
            _oldFullName = oldFullName;
            _newFullName = newFullName;
        }

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var writeName = TryGetDeclarationWriteName(node);
            var members = VisitList(node.Members);
            if (writeName != null)
                node = node.WithName(SyntaxFactory.ParseName(writeName).WithTriviaFrom(node.Name));
            return node.WithMembers(members);
        }

        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var writeName = TryGetDeclarationWriteName(node);
            var members = VisitList(node.Members);
            var usings = VisitList(node.Usings);
            var externs = VisitList(node.Externs);
            if (writeName != null)
                node = node.WithName(SyntaxFactory.ParseName(writeName).WithTriviaFrom(node.Name));
            return node.WithMembers(members).WithUsings(usings).WithExterns(externs);
        }

        public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
        {
            if (node.Name == null)
                return base.VisitUsingDirective(node);

            var symbol = _semanticModel.GetSymbolInfo(node.Name).Symbol as INamespaceSymbol;
            if (IsTargetOrDescendant(symbol))
            {
                var rewritten = RewriteFullName(GetFullName(symbol!), _oldFullName, _newFullName);
                return node.WithName(SyntaxFactory.ParseName(rewritten).WithTriviaFrom(node.Name));
            }

            return base.VisitUsingDirective(node);
        }

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        {
            if (node.Parent is BaseNamespaceDeclarationSyntax or UsingDirectiveSyntax)
                return node;

            // Rewrite the containing qualified name. A dotted replacement must
            // never be substituted into Right (a SimpleNameSyntax slot).
            if (TryRewriteReferencedName(node) is { } rewritten)
                return rewritten;

            return base.VisitQualifiedName(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (TryRewriteReferencedName(node) is { } rewritten)
                return rewritten;

            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is QualifiedNameSyntax or MemberAccessExpressionSyntax)
                return node;
            if (node.Parent is BaseNamespaceDeclarationSyntax or UsingDirectiveSyntax)
                return node;

            var symbol = GetReferencedSymbol(node);
            if (symbol is INamespaceSymbol nsSymbol && SymbolEqualityComparer.Default.Equals(nsSymbol, _target))
            {
                var replacement = SyntaxFactory.ParseName(_newFullName);
                // IdentifierName is a SimpleName slot: only a single identifier fits.
                if (replacement is SimpleNameSyntax simple)
                    return simple.WithTriviaFrom(node);
                return node;
            }

            return base.VisitIdentifierName(node);
        }

        /// <summary>
        /// Replaces the namespace prefix of a qualified type or namespace name.
        /// Uses the bound symbol's containing namespace because intermediate
        /// name nodes often have no <see cref="INamespaceSymbol"/> of their own.
        /// </summary>
        private NameSyntax? TryRewriteReferencedName(ExpressionSyntax node)
        {
            var symbol = GetReferencedSymbol(node);
            var ns = symbol as INamespaceSymbol ?? symbol?.ContainingNamespace;
            if (!IsTargetOrDescendant(ns))
                return null;

            var written = GetDottedNameText(node);
            var rewritten = RewriteFullName(written, _oldFullName, _newFullName);
            if (string.Equals(rewritten, written, StringComparison.Ordinal))
            {
                if (symbol is not INamespaceSymbol onlyNs)
                    return null;

                rewritten = RewriteFullName(GetFullName(onlyNs), _oldFullName, _newFullName);
                if (string.Equals(rewritten, GetFullName(onlyNs), StringComparison.Ordinal))
                    return null;
            }

            return SyntaxFactory.ParseName(rewritten).WithTriviaFrom(node);
        }

        private ISymbol? GetReferencedSymbol(SyntaxNode node)
        {
            var info = _semanticModel.GetSymbolInfo(node);
            if (info.Symbol != null)
                return info.Symbol;
            if (info.CandidateSymbols.Length > 0)
                return info.CandidateSymbols[0];

            var type = _semanticModel.GetTypeInfo(node).Type;
            if (type is { SpecialType: not SpecialType.System_Void } and not IErrorTypeSymbol)
                return type;

            return _semanticModel.GetDeclaredSymbol(node);
        }

        private static string GetDottedNameText(ExpressionSyntax node) =>
            string.Join(".", node.ToString().Split('.', StringSplitOptions.TrimEntries));

        private string? TryGetDeclarationWriteName(BaseNamespaceDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is not INamespaceSymbol declared)
                return null;

            var declaredFull = GetFullName(declared);
            string newFull;
            if (IsTargetOrDescendant(declared))
                newFull = RewriteFullName(declaredFull, _oldFullName, _newFullName);
            else if (IsPrefixOfNamespace(declaredFull, _oldFullName))
                newFull = GetCorrespondingPrefix(_oldFullName, _newFullName, declaredFull);
            else
                return null;

            var enclosing = node.Parent as BaseNamespaceDeclarationSyntax;
            var enclosingNew = GetEnclosingNewFullName(enclosing);
            return GetRelativeNamespaceName(newFull, enclosingNew);
        }

        private string GetEnclosingNewFullName(BaseNamespaceDeclarationSyntax? enclosing)
        {
            if (enclosing == null)
                return string.Empty;

            if (_semanticModel.GetDeclaredSymbol(enclosing) is not INamespaceSymbol declared)
                return string.Empty;

            var declaredFull = GetFullName(declared);
            if (IsTargetOrDescendant(declared))
                return RewriteFullName(declaredFull, _oldFullName, _newFullName);
            if (IsPrefixOfNamespace(declaredFull, _oldFullName))
                return GetCorrespondingPrefix(_oldFullName, _newFullName, declaredFull);
            return declaredFull;
        }

        private bool IsTargetOrDescendant(INamespaceSymbol? symbol)
        {
            if (symbol == null || symbol.IsGlobalNamespace)
                return false;

            var full = GetFullName(symbol);
            if (string.Equals(full, _oldFullName, StringComparison.Ordinal)
                || IsPrefixOfNamespace(_oldFullName, full))
            {
                return true;
            }

            for (var current = symbol; current is { IsGlobalNamespace: false }; current = current.ContainingNamespace)
            {
                if (SymbolEqualityComparer.Default.Equals(current, _target))
                    return true;
            }

            return false;
        }
    }
}

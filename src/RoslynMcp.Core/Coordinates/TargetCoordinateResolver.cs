using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Coordinates;

/// <summary>
/// Represents a single resolved target coordinate in a Roslyn <see cref="Solution"/>.
/// </summary>
/// <param name="DocumentId">The resolved Roslyn <see cref="Microsoft.CodeAnalysis.DocumentId"/>.</param>
/// <param name="Span">The resolved <see cref="Microsoft.CodeAnalysis.Text.TextSpan"/> within the document.</param>
/// <param name="Node">The resolved <see cref="SyntaxNode"/> after applying <see cref="EnclosingNormalization"/>.</param>
/// <param name="Document">The resolved Roslyn <see cref="Microsoft.CodeAnalysis.Document"/>.</param>
/// <param name="Symbol">Optional declared or referenced <see cref="ISymbol"/> associated with <paramref name="Node"/>.</param>
public sealed record ResolvedTarget(
    DocumentId DocumentId,
    TextSpan Span,
    SyntaxNode Node,
    Document Document,
    ISymbol? Symbol = null)
{
    /// <summary>
    /// Alias for <see cref="Span"/>.
    /// </summary>
    public TextSpan TextSpan => Span;

    /// <summary>
    /// Alias for <see cref="Node"/>.
    /// </summary>
    public SyntaxNode SyntaxNode => Node;

    /// <summary>
    /// Deconstructs the resolved target into the canonical <c>(DocumentId, TextSpan, SyntaxNode)</c> triple.
    /// </summary>
    /// <param name="documentId">Resolved document identifier.</param>
    /// <param name="span">Resolved character span.</param>
    /// <param name="node">Resolved syntax node.</param>
    public void Deconstruct(out DocumentId documentId, out TextSpan span, out SyntaxNode node)
    {
        documentId = DocumentId;
        span = Span;
        node = Node;
    }
}

/// <summary>
/// Resolves <see cref="TargetCoordinate"/> values (<c>DocumentScope × Anchor × EnclosingNormalization</c>)
/// against a Roslyn <see cref="Solution"/> to exact <c>(DocumentId, TextSpan, SyntaxNode)</c> targets.
/// </summary>
public static class TargetCoordinateResolver
{
    /// <summary>
    /// Filters all C# documents in <paramref name="solution"/> matching <paramref name="scope"/>.
    /// </summary>
    /// <param name="solution">The Roslyn solution to search.</param>
    /// <param name="scope">Optional document scope filter.</param>
    /// <returns>Ordered list of matching documents.</returns>
    public static IReadOnlyList<Document> ResolveDocuments(Solution solution, DocumentScope? scope)
    {
        ArgumentNullException.ThrowIfNull(solution);
        scope ??= DocumentScope.All;

        var hasProjectFilter = scope.ProjectNames is { Count: > 0 };
        var hasFileFilter = scope.FilePaths is { Count: > 0 };
        var hasGlobFilter = scope.DirectoryGlobs is { Count: > 0 };

        var projectRegexes = hasProjectFilter
            ? scope.ProjectNames!
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => CompileGlobRegex(p.Trim(), isPath: false))
                .ToArray()
            : [];

        var normalizedFileFilters = hasFileFilter
            ? scope.FilePaths!
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(NormalizeForMatch)
                .ToArray()
            : [];

        var globRegexes = hasGlobFilter
            ? scope.DirectoryGlobs!
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => CompileGlobRegex(g.Trim(), isPath: true))
                .ToArray()
            : [];

        var results = new List<Document>();

        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (hasProjectFilter && !MatchesAnyProject(project.Name, projectRegexes))
            {
                continue;
            }

            foreach (var document in project.Documents.OrderBy(d => d.FilePath ?? d.Name, StringComparer.Ordinal))
            {
                var docPath = document.FilePath ?? document.Name;
                if (string.IsNullOrWhiteSpace(docPath))
                {
                    continue;
                }

                if (hasFileFilter && !MatchesAnyFilePath(document, normalizedFileFilters))
                {
                    continue;
                }

                if (hasGlobFilter && !MatchesAnyGlob(document, globRegexes))
                {
                    continue;
                }

                results.Add(document);
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves a <see cref="TargetCoordinate"/> against <paramref name="solution"/> to all matching
    /// <see cref="ResolvedTarget"/> instances.
    /// </summary>
    /// <param name="solution">The Roslyn solution to resolve against.</param>
    /// <param name="coordinate">The target coordinate specification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All resolved targets in deterministic document/position order.</returns>
    public static async Task<IReadOnlyList<ResolvedTarget>> ResolveAsync(
        Solution solution,
        TargetCoordinate coordinate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(coordinate);

        ValidateAnchor(coordinate.Anchor);

        var documents = ResolveDocuments(solution, coordinate.Scope);
        var resolvedTargets = new List<ResolvedTarget>();

        foreach (var document in documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var rawCandidates = await ResolveAnchorInDocumentAsync(
                document,
                root,
                text,
                coordinate.Anchor,
                singleDocumentScope: documents.Count == 1,
                cancellationToken).ConfigureAwait(false);

            foreach (var (rawNode, rawSpan, symbol) in rawCandidates)
            {
                var normalized = ApplyNormalization(rawNode, rawSpan, coordinate.Normalization);
                if (normalized is null)
                {
                    continue;
                }

                var (finalNode, finalSpan) = normalized.Value;
                ISymbol? resolvedSymbol = symbol;
                if (resolvedSymbol is null && coordinate.Normalization != EnclosingNormalization.Exact)
                {
                    var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    resolvedSymbol = semanticModel?.GetDeclaredSymbol(finalNode, cancellationToken);
                }

                resolvedTargets.Add(new ResolvedTarget(
                    document.Id,
                    finalSpan,
                    finalNode,
                    document,
                    resolvedSymbol));
            }
        }

        return resolvedTargets;
    }

    /// <summary>
    /// Resolves a <see cref="TargetCoordinate"/> against <paramref name="solution"/> and requires exactly one match.
    /// </summary>
    /// <param name="solution">The Roslyn solution to resolve against.</param>
    /// <param name="coordinate">The target coordinate specification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The single resolved target.</returns>
    /// <exception cref="RefactoringException">Thrown when zero or multiple targets match.</exception>
    public static async Task<ResolvedTarget> ResolveSingleAsync(
        Solution solution,
        TargetCoordinate coordinate,
        CancellationToken cancellationToken = default)
    {
        var matches = await ResolveAsync(solution, coordinate, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                "TargetCoordinate did not match any syntax node in the scoped solution.");
        }

        if (matches.Count > 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"TargetCoordinate matched {matches.Count} targets; narrow DocumentScope or Anchor.");
        }

        return matches[0];
    }

    /// <summary>
    /// Normalizes a syntax node and span according to <paramref name="normalization"/>.
    /// </summary>
    /// <param name="node">The initial resolved syntax node.</param>
    /// <param name="initialSpan">The initial resolved text span.</param>
    /// <param name="normalization">The enclosing normalization mode.</param>
    /// <returns>The normalized node and span, or <c>null</c> if no enclosing construct of the requested kind exists.</returns>
    public static (SyntaxNode Node, TextSpan Span)? ApplyNormalization(
        SyntaxNode node,
        TextSpan initialSpan,
        EnclosingNormalization normalization)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (normalization)
        {
            case EnclosingNormalization.Exact:
                return (node, initialSpan);

            case EnclosingNormalization.EnclosingStatement:
                {
                    var nonBlockStmt = node.AncestorsAndSelf()
                        .OfType<StatementSyntax>()
                        .FirstOrDefault(s => s is not BlockSyntax);
                    var stmt = nonBlockStmt ?? node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
                    return stmt is not null ? (stmt, stmt.Span) : null;
                }

            case EnclosingNormalization.EnclosingBlock:
                {
                    var block = node.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
                    return block is not null ? (block, block.Span) : null;
                }

            case EnclosingNormalization.EnclosingMember:
                {
                    var callableOrFieldMember = node.AncestorsAndSelf()
                        .OfType<MemberDeclarationSyntax>()
                        .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax
                                             and not BaseNamespaceDeclarationSyntax
                                             and not DelegateDeclarationSyntax);

                    var member = callableOrFieldMember
                        ?? node.AncestorsAndSelf()
                            .OfType<MemberDeclarationSyntax>()
                            .FirstOrDefault(m => m is not BaseNamespaceDeclarationSyntax);

                    return member is not null ? (member, member.Span) : null;
                }

            case EnclosingNormalization.EnclosingType:
                {
                    var typeDecl = node.AncestorsAndSelf()
                        .FirstOrDefault(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                    return typeDecl is not null ? (typeDecl, typeDecl.Span) : null;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(normalization), normalization, "Unknown normalization mode.");
        }
    }

    private static void ValidateAnchor(Anchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if (anchor.LineSpan is { } lineSpan)
        {
            if (lineSpan.StartLine < 1)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidLineNumber,
                    $"StartLine ({lineSpan.StartLine}) must be >= 1.");
            }

            if (lineSpan.StartColumn < 1)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidColumnNumber,
                    $"StartColumn ({lineSpan.StartColumn}) must be >= 1.");
            }

            var endLine = lineSpan.EndLine ?? lineSpan.StartLine;
            var endCol = lineSpan.EndColumn ?? lineSpan.StartColumn;

            if (endLine < lineSpan.StartLine || (endLine == lineSpan.StartLine && endCol < lineSpan.StartColumn))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidLineNumber,
                    $"End position ({endLine}:{endCol}) cannot precede start position ({lineSpan.StartLine}:{lineSpan.StartColumn}).");
            }
        }

        if (anchor.TextSpan is { } textSpan)
        {
            if (textSpan.Start < 0 || textSpan.Length < 0)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidColumnNumber,
                    $"TextSpan start ({textSpan.Start}) and length ({textSpan.Length}) must be non-negative.");
            }
        }

        if (anchor.SyntaxIndex is < 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidColumnNumber,
                $"SyntaxIndex ({anchor.SyntaxIndex}) must be non-negative.");
        }
    }

    private static async Task<IReadOnlyList<(SyntaxNode Node, TextSpan Span, ISymbol? Symbol)>> ResolveAnchorInDocumentAsync(
        Document document,
        SyntaxNode root,
        SourceText text,
        Anchor anchor,
        bool singleDocumentScope,
        CancellationToken cancellationToken)
    {
        if (anchor.LineSpan is { } lineSpan)
        {
            var resolved = ResolveLineSpan(root, text, lineSpan, singleDocumentScope);
            return resolved is not null ? [resolved.Value] : [];
        }

        if (anchor.TextSpan is { } textSpan)
        {
            var resolved = ResolveTextSpan(root, text, textSpan, singleDocumentScope);
            return resolved is not null ? [resolved.Value] : [];
        }

        if (!string.IsNullOrWhiteSpace(anchor.SymbolPath))
        {
            return await ResolveSymbolPathAsync(document, root, anchor.SymbolPath!, cancellationToken)
                .ConfigureAwait(false);
        }

        if (anchor.SyntaxIndex is { } syntaxIndex)
        {
            var resolved = ResolveSyntaxIndex(root, syntaxIndex, anchor.SyntaxKind, singleDocumentScope);
            return resolved is not null ? [resolved.Value] : [];
        }

        return [(root, root.Span, null)];
    }

    private static (SyntaxNode Node, TextSpan Span, ISymbol? Symbol)? ResolveLineSpan(
        SyntaxNode root,
        SourceText text,
        LineSpanAnchor lineSpan,
        bool singleDocumentScope)
    {
        var startLineIndex = lineSpan.StartLine - 1;
        var endLine = lineSpan.EndLine ?? lineSpan.StartLine;
        var endLineIndex = endLine - 1;

        if (startLineIndex < 0 || startLineIndex >= text.Lines.Count ||
            endLineIndex < 0 || endLineIndex >= text.Lines.Count)
        {
            if (singleDocumentScope)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidLineNumber,
                    $"Line span {lineSpan.StartLine}..{endLine} is out of range for document with {text.Lines.Count} lines.");
            }

            return null;
        }

        var startLineInfo = text.Lines[startLineIndex];
        var endLineInfo = text.Lines[endLineIndex];

        var startColOffset = lineSpan.StartColumn - 1;
        var startLineLength = startLineInfo.End - startLineInfo.Start;
        if (startColOffset < 0 || startColOffset > startLineLength)
        {
            if (singleDocumentScope)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidColumnNumber,
                    $"StartColumn {lineSpan.StartColumn} is out of range for line {lineSpan.StartLine}.");
            }

            return null;
        }

        var endColOffset = lineSpan.EndColumn.HasValue
            ? lineSpan.EndColumn.Value - 1
            : (lineSpan.EndLine.HasValue ? endLineInfo.End - endLineInfo.Start : startColOffset);

        var endLineLength = endLineInfo.End - endLineInfo.Start;
        if (endColOffset < 0 || endColOffset > endLineLength)
        {
            if (singleDocumentScope)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidColumnNumber,
                    $"EndColumn {endColOffset + 1} is out of range for line {endLine}.");
            }

            return null;
        }

        var startPos = startLineInfo.Start + startColOffset;
        var endPos = endLineInfo.Start + endColOffset;
        if (endPos < startPos)
        {
            return null;
        }

        var span = TextSpan.FromBounds(startPos, endPos);
        var node = FindInnermostNodeForSpan(root, span);
        var effectiveSpan = span.IsEmpty ? node.Span : span;
        return (node, effectiveSpan, null);
    }

    private static (SyntaxNode Node, TextSpan Span, ISymbol? Symbol)? ResolveTextSpan(
        SyntaxNode root,
        SourceText text,
        TextSpanAnchor textSpan,
        bool singleDocumentScope)
    {
        if (textSpan.Start < 0 || textSpan.Start + textSpan.Length > text.Length)
        {
            if (singleDocumentScope)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidColumnNumber,
                    $"TextSpan [{textSpan.Start}, {textSpan.Start + textSpan.Length}) exceeds document length {text.Length}.");
            }

            return null;
        }

        var span = new TextSpan(textSpan.Start, textSpan.Length);
        var node = FindInnermostNodeForSpan(root, span);
        var effectiveSpan = span.IsEmpty ? node.Span : span;
        return (node, effectiveSpan, null);
    }

    private static async Task<IReadOnlyList<(SyntaxNode Node, TextSpan Span, ISymbol? Symbol)>> ResolveSymbolPathAsync(
        Document document,
        SyntaxNode root,
        string rawSymbolPath,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return [];
        }

        var cleaned = rawSymbolPath.Trim();
        if (cleaned.StartsWith("global::", StringComparison.Ordinal))
        {
            cleaned = cleaned["global::".Length..].Trim();
        }

        if (string.IsNullOrEmpty(cleaned))
        {
            return [];
        }

        var results = new List<(SyntaxNode Node, TextSpan Span, ISymbol? Symbol)>();

        foreach (var node in root.DescendantNodes())
        {
            if (node is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            {
                continue;
            }

            // Avoid duplicate matches on both FieldDeclarationSyntax and its VariableDeclaratorSyntax
            // when a single field is declared: resolve VariableDeclaratorSyntax and promote to FieldDeclarationSyntax
            // or resolve MemberDeclarationSyntax directly.
            var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (symbol is null || symbol is IParameterSymbol or ILocalSymbol or ITypeParameterSymbol)
            {
                continue;
            }

            if (!MatchesSymbolPath(symbol, cleaned))
            {
                continue;
            }

            var targetNode = node is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax fieldDecl } }
                             && fieldDecl.Declaration.Variables.Count == 1
                ? fieldDecl
                : node;

            results.Add((targetNode, targetNode.Span, symbol));
        }

        return results;
    }

    private static bool MatchesSymbolPath(ISymbol symbol, string queryPath)
    {
        var fullPath = BuildDottedSymbolPath(symbol);
        if (string.Equals(fullPath, queryPath, StringComparison.Ordinal))
        {
            return true;
        }

        // Also support matching with parameter signature e.g. Ns.Type.Method(int, string)
        if (queryPath.Contains('(') && symbol is IMethodSymbol methodSymbol)
        {
            var signaturePath = $"{fullPath}({string.Join(", ", methodSymbol.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))})";
            var compactSignaturePath = signaturePath.Replace(" ", "", StringComparison.Ordinal);
            var compactQuery = queryPath.Replace(" ", "", StringComparison.Ordinal);
            if (string.Equals(compactSignaturePath, compactQuery, StringComparison.Ordinal) ||
                compactSignaturePath.EndsWith("." + compactQuery, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Support suffix match on boundary '.' (e.g. "Type.Member" matching "Namespace.Type.Member", or "Member" matching "Namespace.Type.Member")
        if (fullPath.EndsWith("." + queryPath, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string BuildDottedSymbolPath(ISymbol symbol)
    {
        var parts = new Stack<string>();
        ISymbol? current = symbol;

        while (current is not null && current is not INamespaceSymbol { IsGlobalNamespace: true })
        {
            if (current is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && ctor.ContainingType is not null)
            {
                parts.Push(ctor.ContainingType.Name);
            }
            else if (!string.IsNullOrEmpty(current.Name))
            {
                parts.Push(current.Name);
            }

            current = current.ContainingSymbol;
        }

        return string.Join(".", parts);
    }

    private static (SyntaxNode Node, TextSpan Span, ISymbol? Symbol)? ResolveSyntaxIndex(
        SyntaxNode root,
        int syntaxIndex,
        string? syntaxKindFilter,
        bool singleDocumentScope)
    {
        IEnumerable<SyntaxNode> nodes = root.DescendantNodesAndSelf();
        if (!string.IsNullOrWhiteSpace(syntaxKindFilter))
        {
            var kindText = syntaxKindFilter.Trim();
            nodes = nodes.Where(n =>
                string.Equals(n.Kind().ToString(), kindText, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.GetType().Name, kindText, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.GetType().Name, kindText + "Syntax", StringComparison.OrdinalIgnoreCase));
        }

        var nodeList = nodes.ToList();
        if (syntaxIndex < 0 || syntaxIndex >= nodeList.Count)
        {
            if (singleDocumentScope)
            {
                throw new RefactoringException(
                    ErrorCodes.SymbolNotFound,
                    $"SyntaxIndex {syntaxIndex} is out of range (found {nodeList.Count} matching syntax nodes).");
            }

            return null;
        }

        var selected = nodeList[syntaxIndex];
        return (selected, selected.Span, null);
    }

    private static SyntaxNode FindInnermostNodeForSpan(SyntaxNode root, TextSpan span)
    {
        if (!span.IsEmpty)
        {
            return root.FindNode(span, getInnermostNodeForTie: true);
        }

        var position = Math.Clamp(span.Start, 0, Math.Max(0, root.FullSpan.End - 1));
        var token = root.FindToken(position);
        return token.Parent ?? root;
    }

    private static bool MatchesAnyProject(string projectName, Regex[] projectRegexes)
    {
        foreach (var regex in projectRegexes)
        {
            if (regex.IsMatch(projectName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyFilePath(Document document, string[] normalizedFileFilters)
    {
        var rawDocPath = document.FilePath ?? document.Name;
        var normalizedDocPath = NormalizeForMatch(rawDocPath);
        var docName = Path.GetFileName(normalizedDocPath);

        foreach (var filter in normalizedFileFilters)
        {
            if (string.Equals(normalizedDocPath, filter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedDocPath.EndsWith("/" + filter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(docName, filter, StringComparison.OrdinalIgnoreCase) && !filter.Contains('/'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyGlob(Document document, Regex[] globRegexes)
    {
        var rawDocPath = document.FilePath ?? document.Name;
        var normalizedDocPath = NormalizeForMatch(rawDocPath);

        foreach (var regex in globRegexes)
        {
            if (regex.IsMatch(normalizedDocPath))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeForMatch(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(path) && (File.Exists(path) || Directory.Exists(path)))
        {
            trimmed = PathResolver.GetPathComparisonKey(path).Replace('\\', '/');
        }

        return trimmed.TrimEnd('/');
    }

    internal static Regex CompileGlobRegex(string pattern, bool isPath)
    {
        var normalized = pattern.Replace('\\', '/').Trim();
        if (isPath && !normalized.Contains('*') && !normalized.Contains('?') && !normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.TrimEnd('/') + "/**";
        }

        var sb = new System.Text.StringBuilder();
        if (isPath && !normalized.StartsWith('/') && !normalized.StartsWith("**/") && !Regex.IsMatch(normalized, "^[A-Za-z]:/"))
        {
            sb.Append("(?:^|.*/)");
        }
        else
        {
            sb.Append('^');
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*')
            {
                var isDoubleStar = i + 1 < normalized.Length && normalized[i + 1] == '*';
                if (isDoubleStar)
                {
                    i++;
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append(isPath ? "[^/]*" : ".*");
                }
            }
            else if (c == '?')
            {
                sb.Append(isPath ? "[^/]" : ".");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Predicates;
using RoslynMcp.Core.Coordinates;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Single-pass Layer 2 evaluator for <see cref="SymbolPredicate"/> ASTs over a Roslyn <see cref="Solution"/> or <see cref="Compilation"/>.
/// </summary>
public static class SymbolPredicateEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="predicate"/> across <paramref name="solution"/> in a single compilation pass per project.
    /// </summary>
    /// <param name="solution">The Roslyn solution to inspect.</param>
    /// <param name="predicate">The composable symbol predicate tree.</param>
    /// <param name="maxResults">Maximum number of symbols to return before truncation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="SymbolQueryResult"/> containing matched symbols and metadata.</returns>
    public static async Task<SymbolQueryResult> EvaluateAsync(
        Solution solution,
        SymbolPredicate predicate,
        int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(predicate);

        if (maxResults <= 0)
        {
            maxResults = 200;
        }

        var stopwatch = Stopwatch.StartNew();
        var cache = new SymbolAnalysisCache(solution);
        var matched = new List<MatchedSymbolInfo>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var document in project.Documents.OrderBy(d => d.FilePath ?? d.Name, StringComparer.Ordinal))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                var semanticModel = compilation.GetSemanticModel(root.SyntaxTree);
                foreach (var node in EnumerateCandidateDeclarationNodes(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                    if (symbol is null || symbol.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    if (!seenSymbols.Add(symbol))
                    {
                        continue;
                    }

                    if (await MatchesAsync(symbol, document, solution, predicate, cache, cancellationToken).ConfigureAwait(false))
                    {
                        matched.Add(ToMatchedSymbolInfo(symbol, document, node));
                    }
                }
            }
        }

        stopwatch.Stop();

        var ordered = matched
            .OrderBy(m => m.FilePath, StringComparer.Ordinal)
            .ThenBy(m => m.Line)
            .ThenBy(m => m.Column)
            .ThenBy(m => m.FullyQualifiedName, StringComparer.Ordinal)
            .ToList();

        var totalCount = ordered.Count;
        var truncated = totalCount > maxResults;
        var resultSlice = truncated ? ordered.Take(maxResults).ToList() : ordered;

        return new SymbolQueryResult
        {
            Success = true,
            Symbols = resultSlice,
            TotalCount = totalCount,
            Truncated = truncated,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Evaluates whether a single <paramref name="symbol"/> declared in <paramref name="declaringDocument"/>
    /// satisfies <paramref name="predicate"/> within <paramref name="solution"/>.
    /// </summary>
    /// <param name="symbol">The symbol to test.</param>
    /// <param name="declaringDocument">Optional declaring document.</param>
    /// <param name="solution">The containing Roslyn solution.</param>
    /// <param name="predicate">The predicate AST to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the symbol matches; otherwise <c>false</c>.</returns>
    public static Task<bool> EvaluateSymbolAsync(
        ISymbol symbol,
        Document? declaringDocument,
        Solution solution,
        SymbolPredicate predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(predicate);

        var cache = new SymbolAnalysisCache(solution);
        return MatchesAsync(symbol, declaringDocument, solution, predicate, cache, cancellationToken);
    }

    private static async Task<bool> MatchesAsync(
        ISymbol symbol,
        Document? declaringDocument,
        Solution solution,
        SymbolPredicate predicate,
        SymbolAnalysisCache cache,
        CancellationToken cancellationToken)
    {
        // 1. Fast synchronous structural checks
        if (predicate.InDocumentScope is { } scope)
        {
            if (!MatchesDocumentScope(symbol, declaringDocument, scope, cache))
            {
                return false;
            }
        }

        if (predicate.SymbolKindIn is { Count: > 0 } kinds)
        {
            if (!MatchesSymbolKind(symbol, kinds))
            {
                return false;
            }
        }

        if (predicate.AccessibilityIn is { Count: > 0 } accessibilities)
        {
            if (!MatchesAccessibility(symbol, accessibilities))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(predicate.NameMatches))
        {
            if (!MatchesName(symbol, predicate.NameMatches!))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(predicate.NamespaceMatches))
        {
            if (!MatchesNamespace(symbol, predicate.NamespaceMatches!))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(predicate.HasAttribute))
        {
            if (!MatchesAttribute(symbol, predicate.HasAttribute!))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(predicate.ImplementsOrInherits))
        {
            if (!MatchesImplementsOrInherits(symbol, predicate.ImplementsOrInherits!))
            {
                return false;
            }
        }

        // 2. Lazy cross-solution reference/caller checks (only computed if fast checks passed)
        if (predicate.CallerCountRange is { } callerRange)
        {
            var callerCount = await cache.GetCallerCountAsync(symbol, cancellationToken).ConfigureAwait(false);
            if (!callerRange.Contains(callerCount))
            {
                return false;
            }
        }

        if (predicate.ReferencesCountRange is { } refRange)
        {
            var refCount = await cache.GetReferenceCountAsync(symbol, cancellationToken).ConfigureAwait(false);
            if (!refRange.Contains(refCount))
            {
                return false;
            }
        }

        // 3. Boolean combinators
        if (predicate.Not is { } negated)
        {
            if (await MatchesAsync(symbol, declaringDocument, solution, negated, cache, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        if (predicate.All is { Count: > 0 } allChildren)
        {
            foreach (var child in allChildren)
            {
                if (!await MatchesAsync(symbol, declaringDocument, solution, child, cache, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
        }

        if (predicate.Any is { Count: > 0 } anyChildren)
        {
            var anyMatched = false;
            foreach (var child in anyChildren)
            {
                if (await MatchesAsync(symbol, declaringDocument, solution, child, cache, cancellationToken).ConfigureAwait(false))
                {
                    anyMatched = true;
                    break;
                }
            }

            if (!anyMatched)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesDocumentScope(
        ISymbol symbol,
        Document? declaringDocument,
        DocumentScope scope,
        SymbolAnalysisCache cache)
    {
        var allowedDocIds = cache.GetAllowedDocumentIds(scope);
        if (declaringDocument is not null && allowedDocIds.Contains(declaringDocument.Id))
        {
            return true;
        }

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var doc = cache.Solution.GetDocument(syntaxRef.SyntaxTree);
            if (doc is not null && allowedDocIds.Contains(doc.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSymbolKind(ISymbol symbol, IReadOnlyList<string> allowedKinds)
    {
        var mappedKind = SymbolKindMapper.Map(symbol).ToString();
        var isConstructor = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor };
        var isType = symbol is INamedTypeSymbol;

        foreach (var raw in allowedKinds)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var candidate = raw.Trim();
            if (string.Equals(mappedKind, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (isConstructor && string.Equals(candidate, "Constructor", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (isType && (string.Equals(candidate, "Type", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(candidate, "NamedType", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAccessibility(ISymbol symbol, IReadOnlyList<string> allowedAccessibilities)
    {
        var actual = symbol.DeclaredAccessibility;
        var actualString = actual.ToString();

        foreach (var raw in allowedAccessibilities)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalized = raw.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
            if (string.Equals(actualString, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (actual == Accessibility.ProtectedOrInternal &&
                string.Equals(normalized, "ProtectedInternal", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (actual == Accessibility.ProtectedAndInternal &&
                string.Equals(normalized, "PrivateProtected", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesName(ISymbol symbol, string rawPattern)
    {
        var name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && ctor.ContainingType is not null
            ? ctor.ContainingType.Name
            : symbol.Name;

        return MatchesPattern(name, rawPattern.Trim(), allowHierarchicalPrefix: false);
    }

    private static bool MatchesNamespace(ISymbol symbol, string rawPattern)
    {
        var ns = GetNamespaceDisplayString(symbol);
        var pattern = rawPattern.Trim();
        if (pattern.StartsWith("global::", StringComparison.Ordinal))
        {
            pattern = pattern["global::".Length..].Trim();
        }

        return MatchesPattern(ns, pattern, allowHierarchicalPrefix: true);
    }

    private static bool MatchesPattern(string value, string pattern, bool allowHierarchicalPrefix)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        // Explicit regex syntax: /pattern/ or regex:pattern
        if (pattern.Length >= 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            return Regex.IsMatch(value, pattern[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(value, pattern["regex:".Length..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Detect regex metacharacters (such as ^, $, |, [], (), +, \ or .*)
        var hasRegexMeta = pattern.Contains('^') ||
                           pattern.Contains('$') ||
                           pattern.Contains('|') ||
                           pattern.Contains('[') ||
                           pattern.Contains('(') ||
                           pattern.Contains('+') ||
                           pattern.Contains('\\') ||
                           pattern.Contains(".*", StringComparison.Ordinal) ||
                           pattern.Contains(".+", StringComparison.Ordinal);

        if (hasRegexMeta)
        {
            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Detect glob wildcards (* or ?)
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var regex = TargetCoordinateResolver.CompileGlobRegex(pattern, isPath: false);
            return regex.IsMatch(value);
        }

        // Plain identifier or dotted namespace
        if (string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (allowHierarchicalPrefix &&
            value.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool MatchesAttribute(ISymbol symbol, string rawAttribute)
    {
        var target = rawAttribute.Trim();
        if (target.StartsWith("global::", StringComparison.Ordinal))
        {
            target = target["global::".Length..].Trim();
        }

        if (target.StartsWith('[') && target.EndsWith(']') && target.Length > 2)
        {
            target = target[1..^1].Trim();
        }

        var targetShort = target.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase)
            ? target[..^"Attribute".Length]
            : target;
        var targetLong = targetShort + "Attribute";

        foreach (var attr in symbol.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }

            var name = attrClass.Name;
            var fullName = attrClass.ToDisplayString();

            if (MatchesPattern(name, targetShort, allowHierarchicalPrefix: false) ||
                MatchesPattern(name, targetLong, allowHierarchicalPrefix: false) ||
                MatchesPattern(fullName, target, allowHierarchicalPrefix: false) ||
                MatchesPattern(fullName, targetLong, allowHierarchicalPrefix: false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesImplementsOrInherits(ISymbol symbol, string rawTypeOrInterface)
    {
        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            return false;
        }

        var target = rawTypeOrInterface.Trim();
        if (target.StartsWith("global::", StringComparison.Ordinal))
        {
            target = target["global::".Length..].Trim();
        }

        // Check all implemented interfaces
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            if (TypeMatchesNameOrPattern(iface, target))
            {
                return true;
            }
        }

        // Check base type chain
        for (var baseType = typeSymbol.BaseType;
             baseType is not null && baseType.SpecialType != SpecialType.System_Object;
             baseType = baseType.BaseType)
        {
            if (TypeMatchesNameOrPattern(baseType, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TypeMatchesNameOrPattern(INamedTypeSymbol candidate, string target)
    {
        if (MatchesPattern(candidate.Name, target, allowHierarchicalPrefix: false) ||
            MatchesPattern(candidate.OriginalDefinition.Name, target, allowHierarchicalPrefix: false) ||
            MatchesPattern(candidate.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), target, allowHierarchicalPrefix: false) ||
            MatchesPattern(candidate.ToDisplayString(), target, allowHierarchicalPrefix: false) ||
            MatchesPattern(candidate.OriginalDefinition.ToDisplayString(), target, allowHierarchicalPrefix: false))
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<SyntaxNode> EnumerateCandidateDeclarationNodes(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax:
                case DelegateDeclarationSyntax:
                case MethodDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case EventDeclarationSyntax:
                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax or EventFieldDeclarationSyntax } }:
                    yield return node;
                    break;
            }
        }
    }

    private static string GetNamespaceDisplayString(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return string.Empty;
        }

        return ns.ToDisplayString();
    }

    private static MatchedSymbolInfo ToMatchedSymbolInfo(ISymbol symbol, Document document, SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return new MatchedSymbolInfo
        {
            Name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && ctor.ContainingType is not null
                ? ctor.ContainingType.Name
                : symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            Kind = SymbolKindMapper.Map(symbol).ToString(),
            Accessibility = symbol.DeclaredAccessibility.ToString(),
            Namespace = GetNamespaceDisplayString(symbol),
            ProjectName = document.Project.Name,
            FilePath = document.FilePath ?? document.Name,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1
        };
    }

    private sealed class SymbolAnalysisCache
    {
        private readonly Dictionary<DocumentScope, HashSet<DocumentId>> _scopeCache = [];
        private readonly Dictionary<ISymbol, int> _callerCounts = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, int> _referenceCounts = new(SymbolEqualityComparer.Default);

        public SymbolAnalysisCache(Solution solution)
        {
            Solution = solution;
        }

        public Solution Solution { get; }

        public HashSet<DocumentId> GetAllowedDocumentIds(DocumentScope scope)
        {
            if (!_scopeCache.TryGetValue(scope, out var ids))
            {
                var docs = TargetCoordinateResolver.ResolveDocuments(Solution, scope);
                ids = docs.Select(d => d.Id).ToHashSet();
                _scopeCache[scope] = ids;
            }

            return ids;
        }

        public async Task<int> GetCallerCountAsync(ISymbol symbol, CancellationToken cancellationToken)
        {
            if (_callerCounts.TryGetValue(symbol, out var cached))
            {
                return cached;
            }

            var callers = await SymbolFinder.FindCallersAsync(symbol, Solution, cancellationToken).ConfigureAwait(false);
            var count = callers.Sum(c => c.Locations.Count(loc => loc.IsInSource));
            _callerCounts[symbol] = count;
            return count;
        }

        public async Task<int> GetReferenceCountAsync(ISymbol symbol, CancellationToken cancellationToken)
        {
            if (_referenceCounts.TryGetValue(symbol, out var cached))
            {
                return cached;
            }

            var references = await SymbolFinder.FindReferencesAsync(symbol, Solution, cancellationToken).ConfigureAwait(false);
            var count = references.Sum(r => r.Locations.Count());
            _referenceCounts[symbol] = count;
            return count;
        }
    }
}

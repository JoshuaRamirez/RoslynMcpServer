using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Resolves the symbol enclosing a reference / call / diagnostic site (the member, type, or namespace
/// declaration enclosing a position, via <see cref="SemanticModel.GetEnclosingSymbol"/>). Shared by the
/// site filters of <c>find_references</c> / <c>find_callers</c> / <c>get_diagnostics</c>
/// (<see cref="NamespaceSiteScope"/>) and <c>find_references</c> / <c>get_diagnostics</c>
/// <c>accessibilityFilter</c> so every site filter uses the same enclosing-symbol notion. Semantic models are cached per syntax tree so
/// each file is bound at most once per resolver.
/// </summary>
internal sealed class SiteEnclosingSymbolResolver
{
    private readonly WorkspaceContext _context;
    private readonly Dictionary<SyntaxTree, SemanticModel?> _semanticModelCache = new();

    public SiteEnclosingSymbolResolver(WorkspaceContext context)
    {
        _context = context;
    }

    /// <summary>
    /// The symbol enclosing <paramref name="position"/> in <paramref name="tree"/> (whose document is
    /// <paramref name="document"/>, or <c>null</c> for a tree that is not a regular document). Null when
    /// there is no tree, no semantic model, or no enclosing symbol.
    /// </summary>
    public async Task<ISymbol?> GetEnclosingSymbolAsync(
        SyntaxTree? tree, Document? document, int position, CancellationToken cancellationToken)
    {
        var semanticModel = await GetCachedSemanticModelAsync(tree, document, cancellationToken);
        return semanticModel?.GetEnclosingSymbol(position, cancellationToken);
    }

    /// <summary>
    /// Symbol for a diagnostic site at <paramref name="position"/>: the nearest declared symbol
    /// whose syntax contains that position (so a declaration-attached diagnostic such as CS0169 on a
    /// field identifier, or CS0246 on a field's type, resolves to that field rather than the enclosing
    /// type — a field without an initializer does not establish a member semantic scope for
    /// <see cref="SemanticModel.GetEnclosingSymbol"/>), falling back to
    /// <see cref="SemanticModel.GetEnclosingSymbol"/> when no declaration covers the position
    /// (expression / statement body sites). A declaration covers a position only within its
    /// <see cref="SyntaxNode.Span"/>, so trivia before a declaration (e.g. a <c>#warning</c> directive
    /// immediately before <c>public class C</c>) does not inherit that declaration; documentation
    /// comment trivia is the exception and belongs to the declaration it documents. A zero-width
    /// diagnostic at the end of an unterminated declaration that no declaration span covers (e.g. CS1002
    /// for <c>private int field</c> at the end of a file with no trailing newline, where <c>FindToken</c>
    /// lands on the end-of-file token) resolves through that preceding declaration (see
    /// <see cref="GetUnterminatedDeclarationSymbol"/>). Symbols declared by
    /// an expression (anonymous types and their properties, tuple types and their elements) are looked
    /// through to the enclosing source member, so <c>new { P = Missing }</c> in a <c>private</c>
    /// method is a <c>private</c> site. Null when there is no tree, no semantic model, or neither path
    /// yields a symbol.
    /// </summary>
    public async Task<ISymbol?> GetDeclaredOrEnclosingSymbolAsync(
        SyntaxTree? tree, Document? document, int position, CancellationToken cancellationToken)
    {
        var semanticModel = await GetCachedSemanticModelAsync(tree, document, cancellationToken);
        if (semanticModel == null || tree == null)
            return null;

        var root = await tree.GetRootAsync(cancellationToken);
        // FindToken attributes a position in trivia to the token owning that trivia; only a documentation
        // comment is part of the declaration that follows it.
        var inDocumentationComment = IsDocumentationComment(root.FindTrivia(position));
        var token = root.FindToken(position);
        for (SyntaxNode? node = token.Parent; node != null; node = node.Parent)
        {
            var covers = inDocumentationComment
                ? node.FullSpan.Contains(position)
                : node.Span.Contains(position);
            if (!covers)
                continue;

            var declared = GetDeclaredSymbol(semanticModel, node, cancellationToken);
            if (declared != null && !IsSynthesizedExpressionMember(declared))
                return declared;
        }

        if (!inDocumentationComment)
        {
            var unterminated = GetUnterminatedDeclarationSymbol(semanticModel, token, position, cancellationToken);
            if (unterminated != null)
                return unterminated;
        }

        return semanticModel.GetEnclosingSymbol(position, cancellationToken);
    }

    /// <summary>
    /// Symbol of the declaration that ends, unterminated, exactly at <paramref name="position"/>: a
    /// zero-width declaration-boundary diagnostic (e.g. CS1002 for <c>private int field</c> at the end of
    /// a file with no trailing newline or closing brace) sits at the exclusive
    /// <see cref="SyntaxNode.Span"/> end of the declaration it belongs to, and <c>FindToken</c> yields
    /// the following token (the end-of-file token), whose ancestors cover nothing. Walks out from the
    /// non-zero-width token ending exactly at the position (no trivia in between, so a directive or
    /// comment before a declaration never reaches back to the previous one) and returns the innermost
    /// declaration whose span also ends there with a missing (parser-inserted) last token, so a complete
    /// member before an unterminated type (<c>class A { void M() {} </c> at end of file) does not claim
    /// the type's missing <c>}</c>. Null when no such declaration exists.
    /// </summary>
    private static ISymbol? GetUnterminatedDeclarationSymbol(
        SemanticModel semanticModel, SyntaxToken token, int position, CancellationToken cancellationToken)
    {
        // The token found starts at or after the position (e.g. the zero-width end-of-file token) unless
        // the position is in the found token's trailing trivia.
        var preceding = token.SpanStart >= position ? token.GetPreviousToken() : token;
        if (preceding.RawKind == 0 || preceding.Span.Length == 0 || preceding.Span.End != position)
            return null;

        for (SyntaxNode? node = preceding.Parent; node != null; node = node.Parent)
        {
            if (node.Span.End != position || !node.GetLastToken(includeZeroWidth: true).IsMissing)
                continue;

            var declared = GetDeclaredSymbol(semanticModel, node, cancellationToken);
            if (declared != null && !IsSynthesizedExpressionMember(declared))
                return declared;
        }

        return null;
    }

    /// <summary>
    /// Declared symbol for <paramref name="node"/>. A field / event-field declaration declares its
    /// symbols on its variable declarators, so a site on the declaration's type, modifiers, or
    /// attributes resolves through its first declarator (every declarator shares the declaration's
    /// accessibility) instead of walking out to the containing type.
    /// </summary>
    private static ISymbol? GetDeclaredSymbol(
        SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
    {
        if (node is BaseFieldDeclarationSyntax { Declaration.Variables: [var declarator, ..] })
            return semanticModel.GetDeclaredSymbol(declarator, cancellationToken);

        return semanticModel.GetDeclaredSymbol(node, cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="symbol"/> is declared by an expression rather than by a source member:
    /// an anonymous type (<c>new { ... }</c>), one of its synthesized properties (an
    /// <see cref="AnonymousObjectMemberDeclaratorSyntax"/> such as <c>P = Missing</c>), a tuple type
    /// (declared by a <see cref="TupleExpressionSyntax"/>), or a tuple element (a named tuple argument
    /// such as <c>(a: Missing, 1)</c>). Those report a synthesized accessibility (anonymous-type
    /// properties and tuple elements are <c>public</c>) and their containing-symbol chain does not lead
    /// back to the member whose body holds the expression, so the site walk looks through them to the
    /// enclosing source member (e.g. a <c>private</c> method).
    /// </summary>
    private static bool IsSynthesizedExpressionMember(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.IsAnonymousType || type.IsTupleType,
        IPropertySymbol property => property.ContainingType is { IsAnonymousType: true },
        IFieldSymbol field => field.ContainingType is { IsTupleType: true },
        _ => false
    };

    private static bool IsDocumentationComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    private async Task<SemanticModel?> GetCachedSemanticModelAsync(
        SyntaxTree? tree, Document? document, CancellationToken cancellationToken)
    {
        if (tree == null) return null;

        if (!_semanticModelCache.TryGetValue(tree, out var semanticModel))
        {
            semanticModel = await GetSemanticModelAsync(tree, document, cancellationToken);
            _semanticModelCache[tree] = semanticModel;
        }

        return semanticModel;
    }

    /// <summary>
    /// Semantic model for a site tree: from its document when it has one, otherwise (e.g. a
    /// source-generated partial declaration, which is not a regular document) from the compilation of
    /// the project whose generator output contains that exact tree. Compilations are cached by the
    /// workspace, and the locations come from those same compilations.
    /// </summary>
    private async Task<SemanticModel?> GetSemanticModelAsync(
        SyntaxTree tree, Document? document, CancellationToken cancellationToken)
    {
        if (document != null)
            return await document.GetSemanticModelAsync(cancellationToken);

        foreach (var project in _context.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null && compilation.ContainsSyntaxTree(tree))
                return compilation.GetSemanticModel(tree);
        }

        return null;
    }
}

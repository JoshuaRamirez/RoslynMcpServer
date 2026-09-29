using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared replace-existing removal of already-implemented members across
/// partial type parts for <c>implement_abstract</c> and
/// <c>implement_interface</c>. Same body as the prior private copies on
/// <c>ImplementAbstractOperation</c> /
/// <c>ImplementInterfaceOperation</c>.
/// </summary>
internal static class RemoveExistingImplementationsHelpers
{
    /// <summary>
    /// Removes declarations of <paramref name="existingImplementations"/> from
    /// every declaring partial part of <paramref name="typeSymbol"/> in
    /// <paramref name="solution"/> (including multi-variable event-field
    /// declarator rewrites). Uses span rematch so a prior target-type
    /// annotation on the solution root is preserved. KeepDirectives /
    /// KeepExteriorTrivia so a leading <c>#if</c> / <c>#region</c> on a
    /// removed member does not orphan a following <c>#endif</c> /
    /// <c>#endregion</c>.
    /// </summary>
    internal static async Task<Solution> RemoveExistingImplementationsAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IEnumerable<ISymbol> existingImplementations,
        CancellationToken cancellationToken)
    {
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();
        var eventDeclaratorsByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int FieldStart, int DeclaratorStart)>>>();

        foreach (var existing in existingImplementations)
        {
            foreach (var reference in existing.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (EventFieldDeclarator.TryGet(syntax, out var eventField, out var declarator)
                    && eventField.Parent is TypeDeclarationSyntax eventPart)
                {
                    if (eventField.Declaration.Variables.Count > 1)
                    {
                        AddKeyed(eventDeclaratorsByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                            (eventField.SpanStart, declarator.SpanStart));
                        continue;
                    }

                    AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                        (eventField.SpanStart, eventField.Span.End, eventField.Kind()));
                    continue;
                }

                var memberSyntax = RemovableMemberHelpers.AsRemovableMember(syntax);
                if (memberSyntax == null)
                    continue;
                if (memberSyntax.Parent is not TypeDeclarationSyntax part)
                    continue;

                AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, part.SpanStart,
                    (memberSyntax.SpanStart, memberSyntax.Span.End, memberSyntax.Kind()));
            }
        }

        var trees = membersByTreeAndPart.Keys
            .Concat(eventDeclaratorsByTreeAndPart.Keys)
            .Distinct()
            .ToList();

        foreach (var tree in trees)
        {
            var document = DocumentForTreeHelpers.GetDocumentForTree(solution, tree, typeSymbol.Name);
            var treeRoot = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            membersByTreeAndPart.TryGetValue(tree, out var membersByPart);
            eventDeclaratorsByTreeAndPart.TryGetValue(tree, out var eventDeclaratorsByPart);

            var toRemove = new List<MemberDeclarationSyntax>();
            var eventFieldRewrites = new Dictionary<EventFieldDeclarationSyntax, EventFieldDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (!TypePartRematch.SameSyntaxTree(reference.SyntaxTree, tree))
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax originalPart)
                    continue;
                // The solution root may already carry a target-type
                // annotation (new tree). Rematch by span — annotation does
                // not change SpanStart — so RemoveNodes sees nodes from
                // this root and keeps the annotation on the selected type.
                var part = TypePartRematch.RematchTypeDeclaration(treeRoot, originalPart);
                if (part == null)
                    continue;

                HashSet<(int Start, int End, SyntaxKind Kind)>? memberKeys = null;
                HashSet<(int FieldStart, int DeclaratorStart)>? eventDeclaratorKeys = null;
                if (membersByPart != null)
                    membersByPart.TryGetValue(part.SpanStart, out memberKeys);
                if (eventDeclaratorsByPart != null)
                    eventDeclaratorsByPart.TryGetValue(part.SpanStart, out eventDeclaratorKeys);

                foreach (var member in part.Members)
                {
                    if (member is EventFieldDeclarationSyntax eventField
                        && eventDeclaratorKeys != null
                        && eventDeclaratorKeys.Count > 0)
                    {
                        var remaining = eventField.Declaration.Variables
                            .Where(v => !eventDeclaratorKeys.Contains((eventField.SpanStart, v.SpanStart)))
                            .ToList();
                        if (remaining.Count == eventField.Declaration.Variables.Count)
                        {
                            if (memberKeys != null
                                && memberKeys.Contains((eventField.SpanStart, eventField.Span.End, eventField.Kind())))
                            {
                                toRemove.Add(eventField);
                            }

                            continue;
                        }

                        if (remaining.Count == 0)
                        {
                            toRemove.Add(eventField);
                            continue;
                        }

                        eventFieldRewrites[eventField] = eventField.WithDeclaration(
                            eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(remaining)));
                        continue;
                    }

                    if (memberKeys != null
                        && memberKeys.Contains((member.SpanStart, member.Span.End, member.Kind())))
                    {
                        toRemove.Add(member);
                    }
                }
            }

            SyntaxNode newRoot = treeRoot;
            if (eventFieldRewrites.Count > 0)
                newRoot = newRoot.ReplaceNodes(eventFieldRewrites.Keys, (original, _) => eventFieldRewrites[original]);

            if (toRemove.Count > 0)
            {
                // KeepDirectives / KeepExteriorTrivia so a leading #if / #region on
                // the removed member does not orphan a following #endif / #endregion.
                newRoot = newRoot.RemoveNodes(
                        toRemove,
                        SyntaxRemoveOptions.KeepExteriorTrivia | SyntaxRemoveOptions.KeepDirectives)
                    ?? newRoot;
            }

            if (eventFieldRewrites.Count == 0 && toRemove.Count == 0)
                continue;

            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    private static void AddKeyed<T>(
        Dictionary<SyntaxTree, Dictionary<int, HashSet<T>>> map,
        SyntaxTree tree,
        int partSpanStart,
        T key)
    {
        if (!map.TryGetValue(tree, out var byPart))
        {
            byPart = new Dictionary<int, HashSet<T>>();
            map[tree] = byPart;
        }

        if (!byPart.TryGetValue(partSpanStart, out var keys))
        {
            keys = new HashSet<T>();
            byPart[partSpanStart] = keys;
        }

        keys.Add(key);
    }
}

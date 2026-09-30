using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Shared invocation call-site walk for Add/Remove/Reorder parameter ops.
/// </summary>
internal static class SignatureCallSiteHelpers
{
    /// <summary>
    /// Shared invocation call-site walk for Add/Remove/Reorder parameter ops.
    /// Same body as the prior private CollectCallSitesAsync copies; CallSite
    /// construction stays op-local via <paramref name="createCallSite"/>.
    /// </summary>
    internal static async Task<List<TCallSite>> CollectInvocationCallSitesAsync<TCallSite>(
        IReadOnlyList<IMethodSymbol> methods,
        Solution solution,
        Func<Document, InvocationExpressionSyntax, IMethodSymbol, CancellationToken, Task<TCallSite>> createCallSite,
        CancellationToken cancellationToken)
    {
        var callSites = new List<TCallSite>();
        var seen = new HashSet<(DocumentId Id, TextSpan Span)>();

        foreach (var method in methods)
        {
            var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.Location.Kind != LocationKind.SourceFile)
                        continue;

                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null)
                        continue;

                    var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    if (SignatureReferenceHelpers.IsDeclarationName(node, location.Location.SourceSpan))
                        continue;

                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation != null && SignatureReferenceHelpers.IsInvokedMethodName(invocation, location.Location.SourceSpan))
                    {
                        if (!seen.Add((document.Id, invocation.Span)))
                            continue;

                        callSites.Add(await createCallSite(document, invocation, method, cancellationToken));
                        continue;
                    }

                    if (SignatureReferenceHelpers.IsNameOfArgument(node))
                        continue;

                    throw new RefactoringException(
                        ErrorCodes.UnsupportedCallSite,
                        $"Method '{method.Name}' is used as a method group or other unsupported reference and cannot be updated automatically.");
                }
            }
        }

        return callSites;
    }
}

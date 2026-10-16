using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared helpers for <c>make_static</c> / <c>make_non_static</c> params
/// validation, planning, and call-site inspection. Same bodies as the prior private / internal
/// copies on <c>MakeStaticOperation</c> and <c>MakeNonStaticOperation</c>.
/// </summary>
internal static class StaticMethodHelpers
{
    /// <summary>
    /// Shared make_static / make_non_static params gate. Same body as the
    /// prior identical <c>MakeStaticOperation.Validate</c> /
    /// <c>MakeNonStaticOperation.Validate</c> copies. AllFiles rejects any
    /// selection coordinate or <paramref name="symbolName"/>
    /// (<see cref="ErrorCodes.MissingRequiredParam"/>) and does not
    /// shape-check the optional <paramref name="sourceFile"/>. Single-site
    /// requires <paramref name="sourceFile"/> and all four coordinates
    /// (<see cref="ErrorCodes.MissingRequiredParam"/>), then an absolute
    /// <c>.cs</c> path, then 1-based coordinates
    /// (<see cref="ErrorCodes.InvalidLineNumber"/> /
    /// <see cref="ErrorCodes.InvalidColumnNumber"/>), then end after start
    /// (<see cref="ErrorCodes.InvalidSelectionRange"/>), then an existing
    /// file (<see cref="ErrorCodes.SourceFileNotFound"/>).
    /// </summary>
    internal static void ValidateParams(
        bool allFiles,
        string? sourceFile,
        int? startLine,
        int? startColumn,
        int? endLine,
        int? endColumn,
        string? symbolName)
    {
        if (allFiles)
        {
            if (startLine.HasValue ||
                startColumn.HasValue ||
                endLine.HasValue ||
                endColumn.HasValue ||
                !string.IsNullOrWhiteSpace(symbolName))
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with startLine, startColumn, endLine, endColumn, or symbolName.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(sourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!startLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startLine is required.");

        if (!startColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "startColumn is required.");

        if (!endLine.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endLine is required.");

        if (!endColumn.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "endColumn is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(sourceFile);

        if (startLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (startColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (endLine.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (endColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (endLine.Value < startLine.Value ||
            (endLine.Value == startLine.Value && endColumn.Value < startColumn.Value))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");
    }

    /// <summary>
    /// Finds the <see cref="SimpleNameSyntax"/> covering
    /// <paramref name="span"/> (node itself or nearest ancestor/self whose
    /// span contains / is contained by the span). Same body as the prior
    /// private copies.
    /// </summary>
    internal static SimpleNameSyntax? FindReferencedName(SyntaxNode root, TextSpan span)
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        return node as SimpleNameSyntax
            ?? node.AncestorsAndSelf().OfType<SimpleNameSyntax>()
                .FirstOrDefault(name => name.Span.Contains(span) || span.Contains(name.Span));
    }

    /// <summary>
    /// Binding name under a conditional-access <c>WhenNotNull</c> (member
    /// binding or invocation of a member binding). Same body as the prior
    /// private copies.
    /// </summary>
    internal static SimpleNameSyntax? GetConditionalBindingName(ConditionalAccessExpressionSyntax conditional) =>
        conditional.WhenNotNull switch
        {
            MemberBindingExpressionSyntax binding => binding.Name,
            InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax binding =>
                binding.Name,
            _ => null
        };

    /// <summary>
    /// True when <paramref name="name"/> is a null-conditional member binding
    /// (or the binding name of an enclosing conditional access). Same body as
    /// the prior private copies.
    /// </summary>
    internal static bool IsConditionalAccessCallSite(SimpleNameSyntax name) =>
        name.Parent is MemberBindingExpressionSyntax ||
        name.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any(conditional =>
            GetConditionalBindingName(conditional) == name);

    /// <summary>
    /// Compact signature text (modifiers + return type + name + type params +
    /// parameter list) for preview snippets. Same body as the prior private
    /// copies.
    /// </summary>
    internal static string GetSignatureSnippet(MethodDeclarationSyntax method)
    {
        var returnType = method.ReturnType.ToString();
        var modifiers = string.Join(" ", method.Modifiers.Select(token => token.Text));
        var signature = $"{modifiers} {returnType} {method.Identifier}{method.TypeParameterList}{method.ParameterList}";
        return signature.Trim();
    }

    /// <summary>
    /// True when any declaration or call-site span is already present in
    /// <paramref name="claimedSpans"/>. Same body as the prior private
    /// <c>PlanConflictsWithClaimedSpans</c> copies on MakeStatic /
    /// MakeNonStatic (span-pair projection — private <c>StaticPlan</c>
    /// stays on each operation).
    /// </summary>
    internal static bool PlanConflictsWithClaimedSpans(
        IEnumerable<(SyntaxTree Tree, TextSpan Span)> declarationSpans,
        IEnumerable<(SyntaxTree Tree, TextSpan Span)> callSiteSpans,
        HashSet<(SyntaxTree Tree, TextSpan Span)> claimedSpans)
    {
        foreach (var declaration in declarationSpans)
        {
            if (claimedSpans.Contains(declaration))
                return true;
        }

        foreach (var callSite in callSiteSpans)
        {
            if (claimedSpans.Contains(callSite))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Documents in <paramref name="solution"/> that hold each declaration of
    /// <paramref name="method"/>, including both parts of a partial method
    /// (via <see cref="PartialMethodHelpers.EnumerateDeclaringSyntaxReferences"/>),
    /// in declaring-reference order. Throws <see cref="RefactoringException"/>
    /// with <see cref="ErrorCodes.DocumentNotEditable"/> when a declaration's
    /// tree is not in <paramref name="solution"/>, or when there are no
    /// declarations at all. Same body as the prior private
    /// <c>GetDeclarationDocumentsAsync</c> copies on MakeStatic /
    /// MakeNonStatic, which read <c>Context.Solution</c> where this reads
    /// <paramref name="solution"/>.
    /// </summary>
    internal static async Task<IReadOnlyList<Document>> GetDeclarationDocumentsAsync(
        IMethodSymbol method,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var documents = new List<Document>();
        foreach (var reference in PartialMethodHelpers.EnumerateDeclaringSyntaxReferences(method))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(syntax.SyntaxTree);
            if (document == null)
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Declaration of '{method.Name}' is not in an editable document.");
            }

            documents.Add(document);
        }

        if (documents.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Method '{method.Name}' is not in an editable document.");
        }

        return documents;
    }
}

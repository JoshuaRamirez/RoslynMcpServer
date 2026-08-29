using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Removes redundant namespace qualifications from type (and similar)
/// references when a using directive or the current namespace already
/// makes the short name bind to the same symbol (UC-A8 simplify_name).
/// </summary>
public sealed class SimplifyNameOperation : RefactoringOperationBase<SimplifyNameParams>
{
    internal const string ScopeFile = "file";
    internal const string ScopeLocation = "location";

    private const string CandidateIdKind = "simplify-name-id";

    /// <summary>
    /// Creates a new simplify-name operation.
    /// </summary>
    public SimplifyNameOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(SimplifyNameParams @params) => Validate(@params);

    /// <summary>
    /// Validates simplify-name parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(SimplifyNameParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        var scope = NormalizeScope(@params.Scope);

        if (scope == ScopeLocation)
        {
            if (!@params.Line.HasValue)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line is required when scope is location.");

            if (@params.Line.Value < 1)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        }
        else if (@params.Line.HasValue && @params.Line.Value < 1)
        {
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        }

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

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
        SimplifyNameParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not get a semantic model.");

        var scope = NormalizeScope(@params.Scope);
        var candidates = CollectCandidates(root, model);

        SyntaxNode? locationTarget = null;
        if (scope == ScopeLocation)
        {
            locationTarget = FindNameAtLocation(candidates, @params.Line!.Value, @params.Column);
            if (locationTarget == null)
            {
                throw new RefactoringException(
                    ErrorCodes.NoSimplifiableNames,
                    "No names can be simplified at the specified location.");
            }

            candidates = [locationTarget];
        }

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoSimplifiableNames,
                "No names can be simplified.");
        }

        var outcome = await TrySimplifyAsync(document, root, model, candidates, cancellationToken);
        if (outcome.Applied.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoSimplifiableNames,
                scope == ScopeLocation
                    ? "No names can be simplified at the specified location."
                    : "No names can be simplified.");
        }

        var description = BuildDescription(scope, outcome.Applied.Count, outcome.Skipped.Count);
        var beforeSnippet = locationTarget != null
            ? locationTarget.ToString()
            : string.Join(Environment.NewLine, outcome.Applied.Select(item => item.OriginalText));
        var afterSnippet = locationTarget != null
            ? outcome.Applied[0].NewText
            : string.Join(Environment.NewLine, outcome.Applied.Select(item => item.NewText));

        if (@params.Preview)
        {
            var span = (locationTarget ?? root).GetLocation().GetLineSpan();
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = description,
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet,
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                }
            };

            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = pendingChanges,
                Scope = scope,
                SimplificationsApplied = outcome.Applied.Count,
                SimplificationsSkipped = outcome.Skipped.Count,
                SkippedReasons = outcome.Skipped
            };
        }

        var newDocument = document.WithSyntaxRoot(outcome.NewRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            Scope = scope,
            SimplificationsApplied = outcome.Applied.Count,
            SimplificationsSkipped = outcome.Skipped.Count,
            SkippedReasons = outcome.Skipped
        };
    }

    internal static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return ScopeFile;

        var normalized = scope.Trim().ToLowerInvariant();
        if (normalized is ScopeFile or ScopeLocation)
            return normalized;

        throw new RefactoringException(
            ErrorCodes.MissingRequiredParam,
            "scope must be file or location.");
    }

    /// <summary>
    /// Collects outermost qualified type (and similar) names that are not
    /// using-directive or namespace-declaration names.
    /// </summary>
    internal static List<SyntaxNode> CollectQualifiedNameNodes(SyntaxNode root)
    {
        return root.DescendantNodes()
            .Where(node => node is QualifiedNameSyntax or AliasQualifiedNameSyntax)
            .Where(IsOutermostQualifiedName)
            .Where(node => !IsUsingOrNamespaceName(node))
            .ToList();
    }

    /// <summary>
    /// Collects qualified names plus member-access type/namespace qualifiers.
    /// </summary>
    internal static List<SyntaxNode> CollectCandidates(SyntaxNode root, SemanticModel model)
    {
        var candidates = CollectQualifiedNameNodes(root);

        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (IsUsingOrNamespaceName(access))
                continue;

            if (!IsTypeOrNamespace(access, model))
                continue;

            if (access.Parent is MemberAccessExpressionSyntax parent && IsTypeOrNamespace(parent, model))
                continue;

            candidates.Add(access);
        }

        return candidates;
    }

    internal static SyntaxNode? FindNameAtLocation(IReadOnlyList<SyntaxNode> candidates, int line, int? column)
    {
        var onLine = candidates.Where(node => SpanTouchesLine(node, line)).ToList();
        if (onLine.Count == 0)
            return null;

        if (column.HasValue)
        {
            var covering = onLine
                .Where(node => SpanCoversColumn(node.GetLocation().GetLineSpan(), line, column.Value))
                .OrderBy(node => node.Span.Length)
                .ToList();
            return covering.FirstOrDefault();
        }

        return onLine.OrderBy(node => node.SpanStart).First();
    }

    /// <summary>
    /// Classifies why a qualified name was not simplified.
    /// </summary>
    internal static string ClassifySkipReason(SyntaxNode node, SemanticModel model)
    {
        var display = node.ToString();
        if (ContainsGlobalAlias(node) && !ShortNameWouldConflict(node, model))
            return "global:: alias is required to preserve binding";

        if (ShortNameWouldConflict(node, model))
        {
            return display.Contains("global::", StringComparison.Ordinal)
                ? "Would become ambiguous or bind to a different symbol"
                : "Would conflict with local type";
        }

        return "Would become ambiguous or bind to a different symbol";
    }

    internal static string GetRightmostIdentifier(SyntaxNode node)
    {
        return node switch
        {
            QualifiedNameSyntax qualified => GetRightmostIdentifier(qualified.Right),
            AliasQualifiedNameSyntax alias => GetRightmostIdentifier(alias.Name),
            MemberAccessExpressionSyntax access => GetRightmostIdentifier(access.Name),
            GenericNameSyntax generic => generic.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => node.ToString()
        };
    }

    /// <summary>
    /// Shorter name forms for <paramref name="node"/>, shortest first.
    /// Does not include the original text.
    /// </summary>
    internal static IReadOnlyList<ExpressionSyntax> GetShorterForms(SyntaxNode node)
    {
        var forms = new List<ExpressionSyntax>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(ExpressionSyntax form)
        {
            var text = form.ToString();
            if (string.IsNullOrWhiteSpace(text) || !seen.Add(text))
                return;
            if (string.Equals(text, node.ToString(), StringComparison.Ordinal))
                return;
            forms.Add(form);
        }

        var parts = FlattenNameParts(node);
        if (parts.Count >= 2)
        {
            for (var take = 1; take < parts.Count; take++)
                Add(BuildName(parts.TakeLast(take).ToList(), node is MemberAccessExpressionSyntax));
        }

        if (node is AliasQualifiedNameSyntax alias)
        {
            Add(alias.Name);
            foreach (var shorter in GetShorterForms(alias.Name))
                Add(shorter);
        }

        return forms;
    }

    internal static List<SimpleNameSyntax> FlattenNameParts(SyntaxNode node)
    {
        var parts = new List<SimpleNameSyntax>();
        WalkName(node, parts);
        return parts;
    }

    private static void WalkName(SyntaxNode node, List<SimpleNameSyntax> parts)
    {
        switch (node)
        {
            case QualifiedNameSyntax qualified:
                WalkName(qualified.Left, parts);
                parts.Add(qualified.Right);
                break;
            case AliasQualifiedNameSyntax alias:
                WalkName(alias.Name, parts);
                break;
            case MemberAccessExpressionSyntax access:
                WalkName(access.Expression, parts);
                parts.Add(access.Name);
                break;
            case SimpleNameSyntax simple:
                parts.Add(simple);
                break;
        }
    }

    private static ExpressionSyntax BuildName(IReadOnlyList<SimpleNameSyntax> parts, bool memberAccess)
    {
        if (parts.Count == 0)
            throw new ArgumentException("Name parts are required.", nameof(parts));

        if (parts.Count == 1)
            return parts[0].WithoutTrivia();

        if (memberAccess)
        {
            ExpressionSyntax current = parts[0].WithoutTrivia();
            for (var i = 1; i < parts.Count; i++)
            {
                current = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    current,
                    parts[i].WithoutTrivia());
            }

            return current;
        }

        NameSyntax name = parts[0].WithoutTrivia();
        for (var i = 1; i < parts.Count; i++)
            name = SyntaxFactory.QualifiedName(name, parts[i].WithoutTrivia());

        return name;
    }

    private static bool BindsToSameSymbol(
        SemanticModel model,
        SyntaxNode original,
        ExpressionSyntax replacement,
        ISymbol originalSymbol)
    {
        var position = original.SpanStart;
        foreach (var option in new[]
                 {
                     SpeculativeBindingOption.BindAsTypeOrNamespace,
                     SpeculativeBindingOption.BindAsExpression
                 })
        {
            var spec = model.GetSpeculativeSymbolInfo(position, replacement, option);
            if (spec.CandidateReason == CandidateReason.Ambiguous)
                continue;

            if (spec.Symbol != null
                && SymbolEqualityComparer.Default.Equals(spec.Symbol, originalSymbol))
            {
                return true;
            }

            var typeInfo = model.GetSpeculativeTypeInfo(position, replacement, option);
            if (typeInfo.Type != null
                && SymbolEqualityComparer.Default.Equals(typeInfo.Type, originalSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NameofMeaningPreserved(
        SyntaxNode original,
        ExpressionSyntax replacement,
        SemanticModel model,
        ISymbol originalSymbol)
    {
        if (!TryGetNameofInvocation(original, out var nameofInvocation))
            return true;

        var originalValue = model.GetConstantValue(nameofInvocation);
        if (!originalValue.HasValue)
            return BindsToSameSymbol(model, original, replacement, originalSymbol);

        // nameof(A.B.C) and nameof(C) both yield "C". Predefined keywords
        // (nameof(string)) also yield the type name ("String").
        var expected = originalValue.Value as string;
        var rightmost = replacement is PredefinedTypeSyntax predefined
            ? predefined.Keyword.ValueText
            : GetRightmostIdentifier(replacement);

        if (string.Equals(expected, rightmost, StringComparison.Ordinal))
            return true;

        if (originalSymbol is INamedTypeSymbol named
            && string.Equals(expected, named.Name, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static async Task<SimplifyOutcome> TrySimplifyAsync(
        Document document,
        SyntaxNode root,
        SemanticModel model,
        IReadOnlyList<SyntaxNode> candidates,
        CancellationToken cancellationToken)
    {
        var simplifierOutcome = await TrySimplifierAsync(
            document, root, model, candidates, cancellationToken);
        if (simplifierOutcome.Applied.Count > 0)
            return simplifierOutcome;

        return TrySpeculativeRewrite(root, model, candidates);
    }

    private static async Task<SimplifyOutcome> TrySimplifierAsync(
        Document document,
        SyntaxNode root,
        SemanticModel model,
        IReadOnlyList<SyntaxNode> candidates,
        CancellationToken cancellationToken)
    {
        var annotated = root.ReplaceNodes(candidates, (original, _) =>
            original.WithAdditionalAnnotations(
                Simplifier.Annotation,
                new SyntaxAnnotation(CandidateIdKind, original.SpanStart.ToString(CultureInfo.InvariantCulture))));

        var reducedDocument = await Simplifier.ReduceAsync(
            document.WithSyntaxRoot(annotated),
            Simplifier.Annotation,
            cancellationToken: cancellationToken);

        var reducedRoot = await reducedDocument.GetSyntaxRootAsync(cancellationToken);
        if (reducedRoot == null)
            return new SimplifyOutcome(root, [], []);

        var reducedModel = await reducedDocument.GetSemanticModelAsync(cancellationToken);
        if (reducedModel == null)
            return new SimplifyOutcome(root, [], []);

        var applied = new List<AppliedSimplification>();
        foreach (var original in candidates)
        {
            var id = new SyntaxAnnotation(
                CandidateIdKind,
                original.SpanStart.ToString(CultureInfo.InvariantCulture));
            var reducedNode = reducedRoot.GetAnnotatedNodes(id).FirstOrDefault();
            if (reducedNode == null)
                continue;

            var originalText = original.ToString();
            var newText = reducedNode.ToString();
            if (string.Equals(originalText, newText, StringComparison.Ordinal))
                continue;

            if (!SameBinding(original, model, reducedNode, reducedModel))
                continue;

            applied.Add(new AppliedSimplification(original, originalText, newText));
        }

        if (applied.Count == 0)
            return new SimplifyOutcome(root, [], []);

        var skipped = ClassifyUnchanged(candidates, applied, model);
        return new SimplifyOutcome(reducedRoot, applied, skipped);
    }

    private static SimplifyOutcome TrySpeculativeRewrite(
        SyntaxNode root,
        SemanticModel model,
        IReadOnlyList<SyntaxNode> candidates)
    {
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var applied = new List<AppliedSimplification>();
        var skipped = new List<SkippedSimplification>();

        foreach (var original in candidates)
        {
            if (!TryGetSafeReplacement(original, model, out var replacement))
            {
                skipped.Add(new SkippedSimplification
                {
                    Name = original.ToString(),
                    Reason = ClassifySkipReason(original, model)
                });
                continue;
            }

            var rewritten = replacement.WithTriviaFrom(original);
            replacements[original] = rewritten;
            applied.Add(new AppliedSimplification(original, original.ToString(), rewritten.ToString()));
        }

        if (replacements.Count == 0)
            return new SimplifyOutcome(root, [], skipped);

        var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
        return new SimplifyOutcome(newRoot, applied, skipped);
    }

    internal static bool TryGetSafeReplacement(
        SyntaxNode original,
        SemanticModel model,
        out ExpressionSyntax replacement)
    {
        replacement = null!;
        var originalSymbol = GetBoundSymbol(model, original);
        if (originalSymbol == null)
            return false;

        foreach (var form in GetShorterForms(original))
        {
            if (BindsToSameSymbol(model, original, form, originalSymbol)
                && NameofMeaningPreserved(original, form, model, originalSymbol))
            {
                replacement = form;
                return true;
            }
        }

        return false;
    }

    private static List<SkippedSimplification> ClassifyUnchanged(
        IReadOnlyList<SyntaxNode> candidates,
        List<AppliedSimplification> applied,
        SemanticModel model)
    {
        var appliedSet = applied.Select(item => item.Original).ToHashSet();
        return candidates
            .Where(candidate => !appliedSet.Contains(candidate))
            .Select(candidate => new SkippedSimplification
            {
                Name = candidate.ToString(),
                Reason = ClassifySkipReason(candidate, model)
            })
            .ToList();
    }

    private static bool SameBinding(
        SyntaxNode original,
        SemanticModel originalModel,
        SyntaxNode reduced,
        SemanticModel reducedModel)
    {
        var originalSymbol = GetBoundSymbol(originalModel, original);
        var reducedSymbol = GetBoundSymbol(reducedModel, reduced);
        if (!SymbolEqualityComparer.Default.Equals(originalSymbol, reducedSymbol))
            return false;

        if (!TryGetNameofInvocation(original, out var originalNameof) ||
            !TryGetNameofInvocation(reduced, out var reducedNameof))
        {
            return true;
        }

        var originalValue = originalModel.GetConstantValue(originalNameof);
        var reducedValue = reducedModel.GetConstantValue(reducedNameof);
        if (!originalValue.HasValue || !reducedValue.HasValue)
            return SymbolEqualityComparer.Default.Equals(originalSymbol, reducedSymbol);

        return Equals(originalValue.Value, reducedValue.Value);
    }

    private static ISymbol? GetBoundSymbol(SemanticModel model, SyntaxNode node)
    {
        var info = model.GetSymbolInfo(node);
        if (info.Symbol != null)
            return info.Symbol;

        if (info.CandidateSymbols.Length == 1)
            return info.CandidateSymbols[0];

        return model.GetTypeInfo(node).Type;
    }

    private static bool ShortNameWouldConflict(SyntaxNode node, SemanticModel model)
    {
        var original = GetBoundSymbol(model, node);
        var simple = GetRightmostIdentifier(node);
        if (string.IsNullOrEmpty(simple))
            return false;

        foreach (var symbol in model.LookupSymbols(node.SpanStart, name: simple))
        {
            if (SymbolEqualityComparer.Default.Equals(symbol, original))
                continue;

            if (original is ITypeSymbol or INamespaceSymbol &&
                symbol is not (ITypeSymbol or INamespaceSymbol))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsTypeOrNamespace(SyntaxNode node, SemanticModel model)
    {
        var symbol = GetBoundSymbol(model, node);
        return symbol is ITypeSymbol or INamespaceSymbol;
    }

    private static bool IsOutermostQualifiedName(SyntaxNode node) =>
        node.Parent is not QualifiedNameSyntax and not AliasQualifiedNameSyntax;

    private static bool IsUsingOrNamespaceName(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case UsingDirectiveSyntax:
                    return true;
                case BaseNamespaceDeclarationSyntax ns
                    when ns.Name == node || ns.Name.Span.Contains(node.Span):
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsGlobalAlias(SyntaxNode node) =>
        node is AliasQualifiedNameSyntax
            || node.DescendantNodesAndSelf().OfType<AliasQualifiedNameSyntax>().Any()
            || node.ToString().Contains("global::", StringComparison.Ordinal);

    private static bool TryGetNameofInvocation(SyntaxNode node, out InvocationExpressionSyntax invocation)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is InvocationExpressionSyntax candidate
                && candidate.Expression is IdentifierNameSyntax identifier
                && identifier.Identifier.ValueText == "nameof")
            {
                invocation = candidate;
                return true;
            }
        }

        invocation = null!;
        return false;
    }

    private static bool SpanTouchesLine(SyntaxNode node, int line)
    {
        var span = node.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        return line >= startLine && line <= endLine;
    }

    private static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
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

    private static string BuildDescription(string scope, int applied, int skipped)
    {
        var noun = applied == 1 ? "name" : "names";
        var text = scope == ScopeLocation
            ? $"Simplify {applied} {noun} at location"
            : $"Simplify {applied} {noun} in file";

        if (skipped > 0)
            text += $" ({skipped} skipped)";

        return text;
    }

    private sealed record AppliedSimplification(SyntaxNode Original, string OriginalText, string NewText);

    private sealed record SimplifyOutcome(
        SyntaxNode NewRoot,
        List<AppliedSimplification> Applied,
        List<SkippedSimplification> Skipped);
}

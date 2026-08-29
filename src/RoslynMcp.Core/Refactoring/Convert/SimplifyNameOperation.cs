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

    private static async Task<SimplifyOutcome> TrySimplifyAsync(
        Document document,
        SyntaxNode root,
        SemanticModel model,
        IReadOnlyList<SyntaxNode> candidates,
        CancellationToken cancellationToken)
    {
        var annotated = AnnotateCandidates(root, candidates);
        var reducedDocument = await Simplifier.ReduceAsync(
            document.WithSyntaxRoot(annotated),
            Simplifier.Annotation,
            cancellationToken: cancellationToken);

        var reducedRoot = await reducedDocument.GetSyntaxRootAsync(cancellationToken);
        if (reducedRoot == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse simplified file.");

        var reducedModel = await reducedDocument.GetSemanticModelAsync(cancellationToken);
        if (reducedModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not get a semantic model for the simplified file.");

        var applied = new List<AppliedSimplification>();
        var skipped = new List<SkippedSimplification>();
        var rejected = new List<SyntaxNode>();

        foreach (var original in candidates)
        {
            var id = CandidateId(original);
            var reducedNode = reducedRoot.GetAnnotatedNodes(id).FirstOrDefault();
            if (reducedNode == null)
            {
                skipped.Add(new SkippedSimplification
                {
                    Name = original.ToString(),
                    Reason = ClassifySkipReason(original, model)
                });
                continue;
            }

            var originalText = original.ToString();
            var newText = reducedNode.ToString();
            if (string.Equals(originalText, newText, StringComparison.Ordinal))
            {
                skipped.Add(new SkippedSimplification
                {
                    Name = originalText,
                    Reason = ClassifySkipReason(original, model)
                });
                continue;
            }

            if (!SameBinding(original, model, reducedNode, reducedModel))
            {
                rejected.Add(original);
                skipped.Add(new SkippedSimplification
                {
                    Name = originalText,
                    Reason = "Would become ambiguous or bind to a different symbol"
                });
                continue;
            }

            applied.Add(new AppliedSimplification(original, originalText, newText));
        }

        if (rejected.Count > 0 && applied.Count > 0)
        {
            var safe = applied.Select(item => item.Original).ToList();
            var retry = await TrySimplifyAsync(document, root, model, safe, cancellationToken);
            var rejectedNames = rejected.Select(node => node.ToString()).ToHashSet(StringComparer.Ordinal);
            var rejectedSkips = skipped.Where(item => rejectedNames.Contains(item.Name)).ToList();
            retry.Skipped.AddRange(rejectedSkips);
            return retry;
        }

        if (rejected.Count > 0)
        {
            return new SimplifyOutcome(root, [], skipped);
        }

        return new SimplifyOutcome(reducedRoot, applied, skipped);
    }

    private static SyntaxNode AnnotateCandidates(SyntaxNode root, IReadOnlyList<SyntaxNode> candidates)
    {
        var set = candidates.ToHashSet();
        return root.ReplaceNodes(set, (original, _) =>
            original.WithAdditionalAnnotations(Simplifier.Annotation, CandidateId(original)));
    }

    private static SyntaxAnnotation CandidateId(SyntaxNode node) =>
        new(CandidateIdKind, node.SpanStart.ToString(CultureInfo.InvariantCulture));

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

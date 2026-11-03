using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Transactions;
using RoslynMcp.Core.Coordinates;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Layer 4 host for Roslyn <see cref="CodeFixProvider"/>, <see cref="CodeRefactoringProvider"/>,
/// and diagnostic-driven fixes across <c>Document</c>, <c>Project</c>, or <c>Solution</c> scope.
/// </summary>
public static class CodeActionHostRunner
{
    private static readonly ConcurrentBag<CodeFixProvider> RegisteredFixProviders = [];
    private static readonly ConcurrentBag<CodeRefactoringProvider> RegisteredRefactoringProviders = [];

    /// <summary>
    /// Registers an additional Roslyn <see cref="CodeFixProvider"/> with the host.
    /// </summary>
    /// <param name="provider">The code fix provider to register.</param>
    public static void RegisterCodeFixProvider(CodeFixProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisteredFixProviders.Add(provider);
    }

    /// <summary>
    /// Registers an additional Roslyn <see cref="CodeRefactoringProvider"/> with the host.
    /// </summary>
    /// <param name="provider">The code refactoring provider to register.</param>
    public static void RegisterCodeRefactoringProvider(CodeRefactoringProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisteredRefactoringProviders.Add(provider);
    }

    /// <summary>
    /// Executes a <see cref="CodeActionHostOperation"/> across the requested <c>Document</c>, <c>Project</c>, or <c>Solution</c> scope.
    /// </summary>
    /// <param name="solution">Input solution snapshot.</param>
    /// <param name="operation">Code action host operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated solution snapshot.</returns>
    public static async Task<Solution> ExecuteAsync(
        Solution solution,
        CodeActionHostOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(operation);

        var targets = await TargetCoordinateResolver.ResolveAsync(solution, operation.Target, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new RefactoringException(ErrorCodes.SymbolNotFound, "CodeActionHostOperation target did not match any document.");
        }

        var primaryTarget = targets[0];
        var scopeDocuments = GetScopedDocuments(solution, primaryTarget.Document, operation.Scope, targets);

        var updatedSolution = solution;
        foreach (var doc in scopeDocuments)
        {
            var currentDoc = updatedSolution.GetDocument(doc.Id);
            if (currentDoc is null)
            {
                continue;
            }

            var targetSpan = doc.Id == primaryTarget.DocumentId
                ? primaryTarget.Span
                : (await currentDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))?.Span ?? default;

            updatedSolution = await ExecuteOnDocumentAsync(
                updatedSolution,
                currentDoc,
                targetSpan,
                operation.DiagnosticId,
                operation.CodeActionTitle,
                cancellationToken).ConfigureAwait(false);
        }

        return updatedSolution;
    }

    private static IReadOnlyList<Document> GetScopedDocuments(
        Solution solution,
        Document primaryDocument,
        string? scopeMode,
        IReadOnlyList<ResolvedTarget> resolvedTargets)
    {
        var normalizedScope = (scopeMode ?? "Document").Trim();
        if (string.Equals(normalizedScope, "Solution", StringComparison.OrdinalIgnoreCase))
        {
            return solution.Projects
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .SelectMany(p => p.Documents.OrderBy(d => d.FilePath ?? d.Name, StringComparer.Ordinal))
                .ToList();
        }

        if (string.Equals(normalizedScope, "Project", StringComparison.OrdinalIgnoreCase))
        {
            return primaryDocument.Project.Documents
                .OrderBy(d => d.FilePath ?? d.Name, StringComparer.Ordinal)
                .ToList();
        }

        return resolvedTargets
            .Select(t => t.Document)
            .DistinctBy(d => d.Id)
            .ToList();
    }

    private static async Task<Solution> ExecuteOnDocumentAsync(
        Solution solution,
        Document document,
        Microsoft.CodeAnalysis.Text.TextSpan span,
        string? diagnosticId,
        string? codeActionTitle,
        CancellationToken cancellationToken)
    {
        // 1. Try registered Roslyn CodeFixProvider instances first
        if (!string.IsNullOrWhiteSpace(diagnosticId) && !RegisteredFixProviders.IsEmpty)
        {
            var fromFixProvider = await TryRunRegisteredCodeFixProvidersAsync(
                solution,
                document,
                span,
                diagnosticId!,
                codeActionTitle,
                cancellationToken).ConfigureAwait(false);
            if (fromFixProvider is not null)
            {
                return fromFixProvider;
            }
        }

        // 2. Try registered Roslyn CodeRefactoringProvider instances
        if (!RegisteredRefactoringProviders.IsEmpty)
        {
            var fromRefactoringProvider = await TryRunRegisteredRefactoringProvidersAsync(
                document,
                span,
                codeActionTitle,
                cancellationToken).ConfigureAwait(false);
            if (fromRefactoringProvider is not null)
            {
                return fromRefactoringProvider;
            }
        }

        // 3. Built-in compiler diagnostic & refactoring host actions
        var diagKey = diagnosticId?.Trim().ToUpperInvariant() ?? string.Empty;
        var titleKey = codeActionTitle?.Trim() ?? string.Empty;

        if (diagKey is "CS8019" or "IDE0005" ||
            titleKey.Contains("UnusedUsing", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("UnnecessaryUsing", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("Remove unused using", StringComparison.OrdinalIgnoreCase))
        {
            return await RemoveUnusedUsingsAsync(solution, document, cancellationToken).ConfigureAwait(false);
        }

        if (diagKey is "CS0246" or "CS0103" ||
            titleKey.Contains("AddMissingUsing", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("Add using", StringComparison.OrdinalIgnoreCase))
        {
            return await AddMissingUsingsAsync(solution, document, cancellationToken).ConfigureAwait(false);
        }

        if (diagKey is "CS0535" ||
            titleKey.Contains("ImplementInterface", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("Implement interface", StringComparison.OrdinalIgnoreCase))
        {
            return await ImplementMissingInterfaceMembersAsync(solution, document, span, cancellationToken).ConfigureAwait(false);
        }

        if (diagKey is "CS0534" ||
            titleKey.Contains("ImplementAbstract", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("Implement abstract", StringComparison.OrdinalIgnoreCase))
        {
            return await ImplementMissingAbstractMembersAsync(solution, document, span, cancellationToken).ConfigureAwait(false);
        }

        if (diagKey is "IDE0011" ||
            titleKey.Contains("AddBraces", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("Add braces", StringComparison.OrdinalIgnoreCase))
        {
            return await AddBracesToControlStatementsAsync(solution, document, cancellationToken).ConfigureAwait(false);
        }

        if (titleKey.Contains("SortUsings", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("Sort using", StringComparison.OrdinalIgnoreCase))
        {
            return await SortUsingsAsync(solution, document, cancellationToken).ConfigureAwait(false);
        }

        if (titleKey.Contains("FormatDocument", StringComparison.OrdinalIgnoreCase) ||
            titleKey.Contains("Format document", StringComparison.OrdinalIgnoreCase))
        {
            var formattedDoc = await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return formattedDoc.Project.Solution;
        }

        throw new RefactoringException(
            ErrorCodes.InvalidSelection,
            $"No registered or built-in CodeAction matched diagnosticId='{diagnosticId}' / title='{codeActionTitle}'.");
    }

    private static async Task<Solution?> TryRunRegisteredCodeFixProvidersAsync(
        Solution solution,
        Document document,
        Microsoft.CodeAnalysis.Text.TextSpan span,
        string diagnosticId,
        string? codeActionTitle,
        CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null || tree is null)
        {
            return null;
        }

        var matchingDiagnostics = compilation.GetDiagnostics(cancellationToken)
            .Where(d => d.Location.SourceTree == tree &&
                        string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase) &&
                        (span.IsEmpty || d.Location.SourceSpan.IntersectsWith(span)))
            .ToImmutableArray();

        if (matchingDiagnostics.IsEmpty)
        {
            // Synthesize a diagnostic at the target span if the provider fixes a custom analyzer diagnostic ID
            var location = Location.Create(tree, span);
            var descriptor = new DiagnosticDescriptor(
                diagnosticId,
                diagnosticId,
                diagnosticId,
                "CodeFix",
                DiagnosticSeverity.Warning,
                isEnabledByDefault: true);
            matchingDiagnostics = [Diagnostic.Create(descriptor, location)];
        }

        foreach (var provider in RegisteredFixProviders)
        {
            if (!provider.FixableDiagnosticIds.Any(id => string.Equals(id, diagnosticId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var actions = new List<CodeAction>();
            var context = new CodeFixContext(
                document,
                matchingDiagnostics[0].Location.SourceSpan,
                matchingDiagnostics,
                (action, _) => actions.Add(action),
                cancellationToken);

            await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);

            var selectedAction = string.IsNullOrWhiteSpace(codeActionTitle)
                ? actions.FirstOrDefault()
                : actions.FirstOrDefault(a => a.Title.Contains(codeActionTitle, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(a.EquivalenceKey, codeActionTitle, StringComparison.OrdinalIgnoreCase));

            if (selectedAction is not null)
            {
                var operations = await selectedAction.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
                var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
                if (applyOp is not null)
                {
                    return applyOp.ChangedSolution;
                }
            }
        }

        return null;
    }

    private static async Task<Solution?> TryRunRegisteredRefactoringProvidersAsync(
        Document document,
        Microsoft.CodeAnalysis.Text.TextSpan span,
        string? codeActionTitle,
        CancellationToken cancellationToken)
    {
        foreach (var provider in RegisteredRefactoringProviders)
        {
            var actions = new List<CodeAction>();
            var context = new CodeRefactoringContext(
                document,
                span,
                actions.Add,
                cancellationToken);

            await provider.ComputeRefactoringsAsync(context).ConfigureAwait(false);

            var selectedAction = string.IsNullOrWhiteSpace(codeActionTitle)
                ? actions.FirstOrDefault()
                : actions.FirstOrDefault(a => a.Title.Contains(codeActionTitle, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(a.EquivalenceKey, codeActionTitle, StringComparison.OrdinalIgnoreCase));

            if (selectedAction is not null)
            {
                var operations = await selectedAction.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
                var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
                if (applyOp is not null)
                {
                    return applyOp.ChangedSolution;
                }
            }
        }

        return null;
    }

    private static async Task<Solution> RemoveUnusedUsingsAsync(
        Solution solution,
        Document document,
        CancellationToken cancellationToken)
    {
        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null || root.Usings.Count == 0)
        {
            return solution;
        }

        var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
        var cs8019Spans = diagnostics
            .Where(d => d.Id == "CS8019")
            .Select(d => d.Location.SourceSpan)
            .ToList();

        List<UsingDirectiveSyntax> unusedUsings;
        if (cs8019Spans.Count > 0)
        {
            unusedUsings = root.Usings
                .Where(u => cs8019Spans.Any(s => u.Span.IntersectsWith(s)))
                .ToList();
        }
        else
        {
            // Fallback: compute used namespaces across all referenced symbols in the document body
            var usedNamespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in root.Members.SelectMany(m => m.DescendantNodesAndSelf()))
            {
                var sym = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol
                    ?? semanticModel.GetTypeInfo(node, cancellationToken).Type;
                if (sym?.ContainingNamespace is { IsGlobalNamespace: false } ns)
                {
                    usedNamespaces.Add(ns.ToDisplayString());
                }
            }

            unusedUsings = root.Usings
                .Where(u => u.Alias is null &&
                            u.StaticKeyword.IsKind(SyntaxKind.None) &&
                            u.Name is not null &&
                            !usedNamespaces.Contains(u.Name.ToString()))
                .ToList();
        }

        if (unusedUsings.Count == 0)
        {
            return solution;
        }

        var keptUsings = SyntaxFactory.List(root.Usings.Except(unusedUsings));
        var newRoot = root.WithUsings(keptUsings).NormalizeWhitespace();
        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    private static async Task<Solution> AddMissingUsingsAsync(
        Solution solution,
        Document document,
        CancellationToken cancellationToken)
    {
        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return solution;
        }

        var unresolvedDiagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id is "CS0246" or "CS0103")
            .ToList();

        if (unresolvedDiagnostics.Count == 0)
        {
            return solution;
        }

        var existingUsings = root.Usings
            .Where(u => u.Name is not null)
            .Select(u => u.Name!.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var namespacesToAdd = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var diag in unresolvedDiagnostics)
        {
            var token = root.FindToken(diag.Location.SourceSpan.Start);
            var identifierText = token.ValueText;
            if (string.IsNullOrWhiteSpace(identifierText))
            {
                continue;
            }

            var matchingNs = FindNamespaceForType(semanticModel.Compilation, identifierText);
            if (!string.IsNullOrEmpty(matchingNs) && !existingUsings.Contains(matchingNs))
            {
                namespacesToAdd.Add(matchingNs);
            }
        }

        if (namespacesToAdd.Count == 0)
        {
            return solution;
        }

        var newUsings = root.Usings.AddRange(
            namespacesToAdd.Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))));
        var newRoot = root.WithUsings(newUsings).NormalizeWhitespace();
        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    private static string? FindNamespaceForType(Compilation compilation, string typeName)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.GlobalNamespace);

        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var typeMember in ns.GetTypeMembers(typeName))
            {
                if (typeMember.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal &&
                    !ns.IsGlobalNamespace)
                {
                    return ns.ToDisplayString();
                }
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                stack.Push(childNs);
            }
        }

        return null;
    }

    private static async Task<Solution> ImplementMissingInterfaceMembersAsync(
        Solution solution,
        Document document,
        Microsoft.CodeAnalysis.Text.TextSpan span,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return solution;
        }

        var typeDecl = root.FindNode(span).AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl is null)
        {
            return solution;
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken);
        if (typeSymbol is null)
        {
            return solution;
        }

        var generator = SyntaxGenerator.GetGenerator(document);
        var stubNodes = new List<SyntaxNode>();

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (typeSymbol.FindImplementationForInterfaceMember(member) is not null)
                {
                    continue;
                }

                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                {
                    var parameters = method.Parameters
                        .Select(p => generator.ParameterDeclaration(p.Name, generator.TypeExpression(p.Type)))
                        .ToArray();
                    var returnType = method.ReturnsVoid ? null : generator.TypeExpression(method.ReturnType);
                    var body = method.ReturnsVoid
                        ? []
                        : new SyntaxNode[] { generator.ReturnStatement(generator.DefaultExpression(method.ReturnType)) };
                    stubNodes.Add(generator.MethodDeclaration(
                        method.Name,
                        parameters,
                        returnType: returnType,
                        accessibility: Accessibility.Public,
                        statements: body));
                }
                else if (member is IPropertySymbol prop)
                {
                    var propTypeText = prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    var accessors = prop.IsReadOnly ? "{ get; }" : "{ get; set; }";
                    stubNodes.Add(SyntaxFactory.ParseMemberDeclaration($"public {propTypeText} {prop.Name} {accessors}")!);
                }
            }
        }

        if (stubNodes.Count == 0)
        {
            return solution;
        }

        var updatedType = generator.AddMembers(typeDecl, stubNodes);
        var newRoot = root.ReplaceNode(typeDecl, updatedType).NormalizeWhitespace();
        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    private static async Task<Solution> ImplementMissingAbstractMembersAsync(
        Solution solution,
        Document document,
        Microsoft.CodeAnalysis.Text.TextSpan span,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return solution;
        }

        var classDecl = root.FindNode(span).AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault()
            ?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl is null)
        {
            return solution;
        }

        var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken);
        if (classSymbol is null)
        {
            return solution;
        }

        var generator = SyntaxGenerator.GetGenerator(document);
        var existingNames = classSymbol.GetMembers().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var stubNodes = new List<SyntaxNode>();

        for (var baseType = classSymbol.BaseType;
             baseType is not null && baseType.SpecialType != SpecialType.System_Object;
             baseType = baseType.BaseType)
        {
            foreach (var member in baseType.GetMembers().Where(m => m.IsAbstract))
            {
                if (existingNames.Contains(member.Name))
                {
                    continue;
                }

                existingNames.Add(member.Name);
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                {
                    var parameters = method.Parameters
                        .Select(p => generator.ParameterDeclaration(p.Name, generator.TypeExpression(p.Type)))
                        .ToArray();
                    var returnType = method.ReturnsVoid ? null : generator.TypeExpression(method.ReturnType);
                    var body = method.ReturnsVoid
                        ? []
                        : new SyntaxNode[] { generator.ReturnStatement(generator.DefaultExpression(method.ReturnType)) };
                    stubNodes.Add(generator.MethodDeclaration(
                        method.Name,
                        parameters,
                        returnType: returnType,
                        accessibility: method.DeclaredAccessibility,
                        modifiers: DeclarationModifiers.Override,
                        statements: body));
                }
            }
        }

        if (stubNodes.Count == 0)
        {
            return solution;
        }

        var updatedClass = generator.AddMembers(classDecl, stubNodes);
        var newRoot = root.ReplaceNode(classDecl, updatedClass).NormalizeWhitespace();
        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    private static async Task<Solution> AddBracesToControlStatementsAsync(
        Solution solution,
        Document document,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return solution;
        }

        var unbracedIfs = root.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(i => i.Statement is not BlockSyntax)
            .ToList();

        if (unbracedIfs.Count == 0)
        {
            return solution;
        }

        var newRoot = root.ReplaceNodes(
            unbracedIfs,
            (_, rewritten) => rewritten.WithStatement(SyntaxFactory.Block(rewritten.Statement)))
            .NormalizeWhitespace();
        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    private static async Task<Solution> SortUsingsAsync(
        Solution solution,
        Document document,
        CancellationToken cancellationToken)
    {
        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || root.Usings.Count <= 1)
        {
            return solution;
        }

        var sorted = root.Usings
            .OrderByDescending(u =>
            {
                var name = u.Name?.ToString() ?? string.Empty;
                return name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
            })
            .ThenBy(u => u.Name?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var newRoot = root.WithUsings(SyntaxFactory.List(sorted)).NormalizeWhitespace();
        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }
}

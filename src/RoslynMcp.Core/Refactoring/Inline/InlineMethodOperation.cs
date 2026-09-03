using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// Inlines a method by replacing call sites with the method body and optionally removing the method.
/// </summary>
public sealed class InlineMethodOperation : RefactoringOperationBase<InlineMethodParams>
{
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    private static readonly SyntaxAnnotation TargetMethodAnnotation = new("RoslynMcp.InlineMethod.Target");

    /// <summary>
    /// Creates a new inline method operation.
    /// </summary>
    public InlineMethodOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(InlineMethodParams @params) => Validate(@params);

    /// <summary>
    /// Validates inline-method inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(InlineMethodParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IdentifierPattern.IsMatch(@params.MethodName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"'{@params.MethodName}' is not a valid method name.");

        if (@params.Line.HasValue && @params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (@params.CallSiteLocation != null)
        {
            if (string.IsNullOrWhiteSpace(@params.CallSiteLocation.File))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "callSiteLocation.file is required.");

            if (!PathResolver.IsAbsolutePath(@params.CallSiteLocation.File))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "callSiteLocation.file must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.CallSiteLocation.File))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "callSiteLocation.file must be a .cs file.");

            if (@params.CallSiteLocation.Line < 1)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "callSiteLocation.line must be >= 1.");

            if (@params.CallSiteLocation.Column < 1)
                throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "callSiteLocation.column must be >= 1.");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        InlineMethodParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        var methodSyntax = FindMethodDeclaration(root, @params);
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodSyntax, cancellationToken) as IMethodSymbol
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        ValidateInlineable(methodSyntax, methodSymbol, semanticModel, cancellationToken);

        var callSites = await FindCallSitesAsync(methodSymbol, methodSyntax, document, @params, cancellationToken);
        if (callSites.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoCallSitesFound,
                $"No call sites found for method '{@params.MethodName}'.");
        }

        await ValidateCallSitesAsync(methodSyntax, methodSymbol, callSites, cancellationToken);

        var removeMethod = @params.RemoveMethod && @params.CallSiteLocation == null;
        var newSolution = await ApplyInliningAsync(
            document,
            methodSyntax,
            methodSymbol,
            callSites,
            removeMethod,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                callSites.Count,
                removeMethod,
                cancellationToken);
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
            new Contracts.Models.SymbolInfo
            {
                Name = @params.MethodName,
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            callSites.Count,
            0);
    }

    internal static MethodDeclarationSyntax FindMethodDeclaration(SyntaxNode root, InlineMethodParams @params)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == @params.MethodName)
            .ToList();

        if (methods.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{@params.MethodName}' not found.");
        }

        // Line is required when more than one method matches, even if
        // column is set. Column without Line is not a source position:
        // FindMethod would substitute each candidate's own start line and
        // could silently pick the shortest equally-aligned overload.
        // When both are set, pick by identifier/declaration span and do
        // not require the declaration to start on `line` (continuation-
        // line identifier).
        if (methods.Count > 1 && !@params.Line.HasValue)
        {
            var lines = methods
                .Select(StartLine)
                .ToList();
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
        }

        if (@params.Column.HasValue)
        {
            var covering = FindMethod(root, @params.MethodName, @params.Line, @params.Column);
            if (covering == null)
            {
                throw new RefactoringException(
                    ErrorCodes.MethodNotFound,
                    @params.Line.HasValue
                        ? $"Method '{@params.MethodName}' not found at line {@params.Line}."
                        : $"Method '{@params.MethodName}' not found.");
            }

            return covering;
        }

        // Omitted column keeps today's MethodName + optional Line identifier
        // start-line pick exactly. Do not force column 1. Do not rewrite
        // line-only to covering-span. A single name match with no line is
        // used as-is; line filters identifier start-line; several start-line
        // hits stay SymbolAmbiguous (do not FirstOrDefault the first same-
        // line overload).
        if (methods.Count == 1 && !@params.Line.HasValue)
            return methods[0];

        IEnumerable<MethodDeclarationSyntax> filtered = methods;
        if (@params.Line.HasValue)
        {
            filtered = filtered.Where(m => StartLine(m) == @params.Line.Value);
        }

        var matches = filtered.ToList();
        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                @params.Line.HasValue
                    ? $"Method '{@params.MethodName}' not found at line {@params.Line}."
                    : $"Method '{@params.MethodName}' not found.");
        }

        var optionLines = matches
            .Select(StartLine)
            .ToList();
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", optionLines)}");
    }

    /// <summary>
    /// Finds a method. Omitted <paramref name="column"/> keeps today's
    /// MethodName + optional Line identifier start-line pick (a single name
    /// match with no line is used as-is; line uses identifier start-line
    /// equality; several start-line hits stay ambiguous at the caller). When
    /// set, picks the smallest method whose identifier or declaration span
    /// covers that 1-based column. Do not require the declaration to start
    /// on <paramref name="line"/> when column is set — a split signature may
    /// put the identifier on a continuation line.
    /// </summary>
    internal static MethodDeclarationSyntax? FindMethod(
        SyntaxNode root,
        string methodName,
        int? line,
        int? column)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (column.HasValue)
        {
            // When column is set, do not require the declaration to start
            // on `line` — a split signature's identifier may live on a
            // continuation line whose declaration span still covers that
            // column. Prefer the identifier hit, then the smallest
            // containing declaration. Do not silently pick the first when
            // a covering node exists elsewhere — scan every candidate,
            // including those that do not start on `line`. If nothing
            // covers this position, keep today's not-found (null).
            return methods
                .Where(m => MethodCoversColumn(m, line ?? StartLine(m), column.Value))
                .OrderBy(m => IdentifierCoversColumn(m, line ?? StartLine(m), column.Value) ? 0 : 1)
                .ThenBy(m => m.Span.Length)
                .FirstOrDefault();
        }

        if (methods.Count == 1 && !line.HasValue)
            return methods[0];

        if (!line.HasValue)
            return methods.Count == 1 ? methods[0] : null;

        var startLineMatches = methods.Where(m => StartLine(m) == line.Value).ToList();
        return startLineMatches.Count == 1 ? startLineMatches[0] : null;
    }

    private static int StartLine(MethodDeclarationSyntax method) =>
        method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool MethodCoversColumn(MethodDeclarationSyntax method, int line, int column) =>
        IdentifierCoversColumn(method, line, column) ||
        SpanCoversColumn(method.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(MethodDeclarationSyntax method, int line, int column) =>
        SpanCoversColumn(method.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent method also
    /// match the previous declaration. Same helper as
    /// <c>ChangeReturnTypeOperation.SpanCoversColumn</c> /
    /// <c>ChangeSignatureOperation.SpanCoversColumn</c> /
    /// <c>AddParameterOperation.SpanCoversColumn</c> /
    /// <c>RemoveParameterOperation.SpanCoversColumn</c> /
    /// <c>ReorderParametersOperation.SpanCoversColumn</c>.
    /// </summary>
    internal static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column >= endCol)
            return false;
        return true;
    }

    private static void ValidateInlineable(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (methodSymbol.IsAbstract || methodSymbol.IsExtern ||
            (methodSyntax.Body == null && methodSyntax.ExpressionBody == null))
        {
            throw new RefactoringException(
                ErrorCodes.MethodHasNoBody,
                $"Method '{methodSymbol.Name}' has no body and cannot be inlined.");
        }

        if (methodSymbol.IsVirtual || methodSymbol.IsOverride || methodSymbol.IsAbstract)
        {
            throw new RefactoringException(
                ErrorCodes.MethodIsVirtual,
                $"Virtual, override, or abstract method '{methodSymbol.Name}' cannot be inlined.");
        }

        if (IsInterfaceImplementation(methodSymbol))
        {
            throw new RefactoringException(
                ErrorCodes.MethodIsVirtual,
                $"Method '{methodSymbol.Name}' implements an interface and cannot be inlined.");
        }

        if (IsRecursive(methodSyntax, methodSymbol, semanticModel, cancellationToken))
        {
            throw new RefactoringException(
                ErrorCodes.MethodIsRecursive,
                $"Recursive method '{methodSymbol.Name}' cannot be inlined.");
        }

        if (HasUnsupportedControlFlow(methodSyntax))
        {
            throw new RefactoringException(
                ErrorCodes.UnresolvableControlFlow,
                $"Method '{methodSymbol.Name}' contains nested return, break, continue, goto, or yield and cannot be inlined.");
        }
    }

    private static bool HasUnsupportedControlFlow(MethodDeclarationSyntax methodSyntax)
    {
        SyntaxNode? body = methodSyntax.Body ?? (SyntaxNode?)methodSyntax.ExpressionBody;
        if (body == null)
            return false;

        foreach (var node in body.DescendantNodes())
        {
            if (node is YieldStatementSyntax or GotoStatementSyntax
                or BreakStatementSyntax or ContinueStatementSyntax)
            {
                return true;
            }

            if (node is ReturnStatementSyntax returnStatement &&
                methodSyntax.Body != null &&
                returnStatement.Parent != methodSyntax.Body)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInterfaceImplementation(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Length > 0)
            return true;

        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(implementation, method))
                    return true;
            }
        }

        return false;
    }

    private static bool IsRecursive(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var invoked = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            if (invoked == null)
                continue;

            if (SymbolEqualityComparer.Default.Equals(invoked.OriginalDefinition, methodSymbol.OriginalDefinition))
                return true;
        }

        return false;
    }

    private async Task<List<CallSite>> FindCallSitesAsync(
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodSyntax,
        Document declaringDocument,
        InlineMethodParams @params,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol,
            Context.Solution,
            cancellationToken);

        var callSites = new List<CallSite>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Location.Kind != LocationKind.SourceFile)
                    continue;

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan);
                if (IsMethodDeclarationReference(node, methodSyntax, declaringDocument, document, location.Location))
                    continue;

                var invocation = FindInvokedCall(node, location.Location.SourceSpan);
                if (invocation == null)
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"Method '{methodSymbol.Name}' is used as a method group or non-invocation reference and cannot be inlined.");
                }

                var model = await document.GetSemanticModelAsync(cancellationToken)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not get semantic model.");
                var invoked = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                if (invoked == null ||
                    !SymbolEqualityComparer.Default.Equals(invoked.OriginalDefinition, methodSymbol.OriginalDefinition))
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"Method '{methodSymbol.Name}' is used as a method group or non-invocation reference and cannot be inlined.");
                }

                if (document.Id == declaringDocument.Id && methodSyntax.Span.Contains(invocation.Span))
                    continue;

                if (@params.CallSiteLocation != null &&
                    !MatchesCallSiteLocation(document, invocation, @params.CallSiteLocation))
                {
                    continue;
                }

                callSites.Add(new CallSite(document, invocation.Span));
            }
        }

        return callSites;
    }

    private static bool IsMethodDeclarationReference(
        SyntaxNode node,
        MethodDeclarationSyntax methodSyntax,
        Document declaringDocument,
        Document document,
        Location location)
    {
        if (document.Id != declaringDocument.Id)
            return false;

        if (methodSyntax.Identifier.Span.Contains(location.SourceSpan) ||
            location.SourceSpan.Contains(methodSyntax.Identifier.Span))
        {
            return true;
        }

        return node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Span == methodSyntax.Identifier.Span);
    }

    private static InvocationExpressionSyntax? FindInvokedCall(SyntaxNode node, TextSpan referenceSpan)
    {
        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation == null)
            return null;

        var invokedName = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => (SyntaxNode)identifier,
            GenericNameSyntax generic => generic,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
            _ => invocation.Expression
        };

        if (invokedName.Span.Contains(referenceSpan) || referenceSpan.Contains(invokedName.Span))
            return invocation;

        return null;
    }

    internal static bool MatchesCallSiteLocation(
        Document document,
        InvocationExpressionSyntax invocation,
        CallSiteLocation location)
    {
        if (PathResolver.NormalizePath(document.FilePath ?? "") != PathResolver.NormalizePath(location.File))
            return false;

        var span = invocation.GetLocation().GetLineSpan();
        return SpanCoversColumn(span, location.Line, location.Column);
    }

    private static async Task ValidateCallSitesAsync(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        IReadOnlyList<CallSite> callSites,
        CancellationToken cancellationToken)
    {
        var usageCounts = CountParameterUsages(methodSyntax, methodSymbol);

        foreach (var site in callSites)
        {
            var root = await site.Document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            var invocation = RematchInvocation(root, site.Span)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Call site disappeared from document.");
            var model = await site.Document.GetSemanticModelAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not get semantic model.");

            ValidateReceiver(methodSymbol, invocation);
            ValidateArgumentEvaluation(methodSyntax, methodSymbol, invocation, model, usageCounts);
        }
    }

    private static void ValidateReceiver(IMethodSymbol methodSymbol, InvocationExpressionSyntax invocation)
    {
        if (methodSymbol.IsStatic)
            return;

        var receiver = invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            MemberBindingExpressionSyntax => invocation.Parent,
            _ => null
        };

        if (receiver == null || receiver is ThisExpressionSyntax)
            return;

        throw new RefactoringException(
            ErrorCodes.InvalidSelection,
            $"Cannot inline instance method '{methodSymbol.Name}' at a call site with a different receiver.");
    }

    private static void ValidateArgumentEvaluation(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        IReadOnlyDictionary<string, int> usageCounts)
    {
        var arguments = MapArguments(methodSyntax, methodSymbol, invocation);
        foreach (var (parameterName, expression) in arguments)
        {
            usageCounts.TryGetValue(parameterName, out var uses);
            var safe = MemberAnalyzer.IsSafeToInline(expression, model);
            if (safe)
                continue;

            if (uses != 1)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotInlineSideEffects,
                    $"Cannot inline method '{methodSymbol.Name}': argument for '{parameterName}' has side effects and is not used exactly once.");
            }
        }
    }

    private static Dictionary<string, ExpressionSyntax> MapArguments(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        InvocationExpressionSyntax invocation)
    {
        var map = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        var arguments = invocation.ArgumentList.Arguments;

        for (var i = 0; i < methodSymbol.Parameters.Length; i++)
        {
            var parameter = methodSymbol.Parameters[i];
            ArgumentSyntax? argument = null;

            foreach (var candidate in arguments)
            {
                if (candidate.NameColon != null &&
                    candidate.NameColon.Name.Identifier.Text == parameter.Name)
                {
                    argument = candidate;
                    break;
                }
            }

            if (argument == null && i < arguments.Count && arguments[i].NameColon == null)
                argument = arguments[i];

            if (argument != null)
            {
                map[parameter.Name] = argument.Expression;
                continue;
            }

            var defaultSyntax = methodSyntax.ParameterList.Parameters
                .FirstOrDefault(p => p.Identifier.Text == parameter.Name)?
                .Default?.Value;

            if (defaultSyntax != null)
                map[parameter.Name] = defaultSyntax;
        }

        return map;
    }

    private static Dictionary<string, int> CountParameterUsages(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol)
    {
        var counts = methodSymbol.Parameters.ToDictionary(p => p.Name, _ => 0, StringComparer.Ordinal);
        SyntaxNode? body = methodSyntax.Body ?? (SyntaxNode?)methodSyntax.ExpressionBody;
        if (body == null)
            return counts;

        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (counts.ContainsKey(identifier.Identifier.Text))
                counts[identifier.Identifier.Text]++;
        }

        return counts;
    }

    private static async Task<Solution> ApplyInliningAsync(
        Document declaringDocument,
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        IReadOnlyList<CallSite> callSites,
        bool removeMethod,
        CancellationToken cancellationToken)
    {
        var solution = declaringDocument.Project.Solution;

        var declaringRoot = await declaringDocument.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        var currentMethod = RematchMethod(declaringRoot, methodSyntax)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Method declaration disappeared.");
        declaringRoot = declaringRoot.ReplaceNode(
            currentMethod,
            currentMethod.WithAdditionalAnnotations(TargetMethodAnnotation));
        solution = declaringDocument.WithSyntaxRoot(declaringRoot).Project.Solution;

        var sitesByDocument = callSites.GroupBy(s => s.Document.Id);
        foreach (var group in sitesByDocument)
        {
            var document = solution.GetDocument(group.Key)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var invocations = RematchInvocations(root, group.Select(s => s.Span));
            var rewriter = new InlineCallSiteRewriter(invocations, methodSyntax, methodSymbol);
            var newRoot = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite call sites.");

            if (removeMethod && document.Id == declaringDocument.Id)
            {
                newRoot = RemoveAnnotatedMethod(newRoot, methodSyntax.Identifier.Text);
            }

            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        if (removeMethod && callSites.All(s => s.Document.Id != declaringDocument.Id))
        {
            var document = solution.GetDocument(declaringDocument.Id)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Declaring document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            var newRoot = RemoveAnnotatedMethod(root, methodSyntax.Identifier.Text);
            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        return solution;
    }

    private static MethodDeclarationSyntax? RematchMethod(SyntaxNode root, MethodDeclarationSyntax original)
    {
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Span == original.Span && m.Identifier.Text == original.Identifier.Text);
    }

    private static List<InvocationExpressionSyntax> RematchInvocations(SyntaxNode root, IEnumerable<TextSpan> spans)
    {
        var spanSet = spans.ToHashSet();
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => spanSet.Contains(invocation.Span))
            .ToList();
    }

    private static InvocationExpressionSyntax? RematchInvocation(SyntaxNode root, TextSpan span)
    {
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => invocation.Span == span);
    }

    private static SyntaxNode RemoveAnnotatedMethod(SyntaxNode root, string methodName)
    {
        var annotated = root.GetAnnotatedNodes(TargetMethodAnnotation)
            .OfType<MethodDeclarationSyntax>()
            .ToList();

        if (annotated.Count == 1)
        {
            return root.RemoveNode(annotated[0], SyntaxRemoveOptions.KeepDirectives) ?? root;
        }

        var sameName = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (sameName.Count != 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Could not uniquely identify method '{methodName}' to remove after inlining. Provide a line number to select the overload.");
        }

        return root.RemoveNode(sameName[0], SyntaxRemoveOptions.KeepDirectives) ?? root;
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        InlineMethodParams @params,
        Document originalDocument,
        Solution newSolution,
        int callSiteCount,
        bool removeMethod,
        CancellationToken cancellationToken)
    {
        var pendingChanges = new List<PendingChange>();
        var originalSolution = originalDocument.Project.Solution;

        foreach (var projectChanges in newSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = originalSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                if (oldDoc?.FilePath == null || newDoc == null)
                    continue;

                var before = await oldDoc.GetTextAsync(cancellationToken);
                var after = await newDoc.GetTextAsync(cancellationToken);
                pendingChanges.Add(new PendingChange
                {
                    File = oldDoc.FilePath,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Inline method '{@params.MethodName}' ({callSiteCount} call site(s)" +
                                  (removeMethod ? ", method removed" : "") + ")",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Inline method '{@params.MethodName}' ({callSiteCount} call site(s))",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record CallSite(Document Document, TextSpan Span);

    private sealed class InlineCallSiteRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<InvocationExpressionSyntax> _targets;
        private readonly MethodDeclarationSyntax _methodSyntax;
        private readonly IMethodSymbol _methodSymbol;

        public InlineCallSiteRewriter(
            IReadOnlyList<InvocationExpressionSyntax> targets,
            MethodDeclarationSyntax methodSyntax,
            IMethodSymbol methodSymbol)
        {
            _targets = new HashSet<InvocationExpressionSyntax>(targets);
            _methodSyntax = methodSyntax;
            _methodSymbol = methodSymbol;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var rewritten = new List<StatementSyntax>();
            var changed = false;

            foreach (var statement in node.Statements)
            {
                if (statement is ExpressionStatementSyntax expressionStatement &&
                    TryGetTargetInvocation(expressionStatement.Expression, out var invocation))
                {
                    var inlined = CreateInlinedStatements(invocation, expressionStatement);
                    rewritten.AddRange(inlined);
                    changed = true;
                    continue;
                }

                var visited = (StatementSyntax?)base.Visit(statement);
                if (visited != null)
                    rewritten.Add(visited);
                if (!ReferenceEquals(visited, statement))
                    changed = true;
            }

            return changed ? node.WithStatements(SyntaxFactory.List(rewritten)) : node;
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            if (!TryGetTargetInvocation(node.Expression, out var invocation))
                return base.VisitExpressionStatement(node);

            // In-block calls are spliced by VisitBlock so multiple statements stay in the caller block.
            if (node.Parent is BlockSyntax)
                return node;

            var inlined = CreateInlinedStatements(invocation, node);
            if (inlined.Count == 0)
                return SyntaxFactory.Block().WithTriviaFrom(node);
            if (inlined.Count == 1)
                return inlined[0];

            return SyntaxFactory.Block(inlined);
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (!_targets.Contains(node))
                return base.VisitInvocationExpression(node);

            // Statement-level calls are handled in VisitBlock / VisitExpressionStatement.
            if (node.Parent is ExpressionStatementSyntax)
                return node;

            var expression = CreateInlinedExpression(node);
            return ParenthesizeIfNeeded(expression, node.Parent).WithTriviaFrom(node);
        }

        private bool TryGetTargetInvocation(ExpressionSyntax expression, out InvocationExpressionSyntax invocation)
        {
            if (expression is InvocationExpressionSyntax direct && _targets.Contains(direct))
            {
                invocation = direct;
                return true;
            }

            invocation = null!;
            return false;
        }

        private IReadOnlyList<StatementSyntax> CreateInlinedStatements(
            InvocationExpressionSyntax invocation,
            ExpressionStatementSyntax originalStatement)
        {
            var parameterMap = MapArguments(_methodSyntax, _methodSymbol, invocation);
            var statements = TransformToStatements(parameterMap);

            if (statements.Count == 0)
                return Array.Empty<StatementSyntax>();

            var result = new List<StatementSyntax>(statements.Count);
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i].NormalizeWhitespace();
                if (i == 0)
                {
                    statement = statement
                        .WithLeadingTrivia(originalStatement.GetLeadingTrivia())
                        .WithTrailingTrivia(originalStatement.GetTrailingTrivia());
                }
                else
                {
                    statement = statement.WithLeadingTrivia(originalStatement.GetLeadingTrivia());
                }

                result.Add(statement);
            }

            return result;
        }

        private ExpressionSyntax CreateInlinedExpression(InvocationExpressionSyntax invocation)
        {
            var parameterMap = MapArguments(_methodSyntax, _methodSymbol, invocation);
            return TransformToExpression(parameterMap);
        }

        private IReadOnlyList<StatementSyntax> TransformToStatements(
            IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
        {
            if (_methodSyntax.ExpressionBody != null)
            {
                var expression = Substitute(_methodSyntax.ExpressionBody.Expression, parameterMap);
                return new[] { SyntaxFactory.ExpressionStatement(expression) };
            }

            var body = _methodSyntax.Body
                ?? throw new RefactoringException(ErrorCodes.MethodHasNoBody, "Method has no body.");

            var statements = new List<StatementSyntax>();
            foreach (var statement in body.Statements)
            {
                if (statement is ReturnStatementSyntax returnStatement && returnStatement.Expression == null)
                    continue;

                var substituted = Substitute(statement, parameterMap);
                if (substituted is ReturnStatementSyntax substitutedReturn && substitutedReturn.Expression != null)
                {
                    statements.Add(SyntaxFactory.ExpressionStatement(substitutedReturn.Expression)
                        .WithTriviaFrom(substitutedReturn));
                }
                else
                {
                    statements.Add(substituted);
                }
            }

            return statements;
        }

        private ExpressionSyntax TransformToExpression(
            IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
        {
            if (_methodSyntax.ExpressionBody != null)
                return Substitute(_methodSyntax.ExpressionBody.Expression, parameterMap);

            var body = _methodSyntax.Body
                ?? throw new RefactoringException(ErrorCodes.MethodHasNoBody, "Method has no body.");

            if (body.Statements.Count == 1 &&
                body.Statements[0] is ReturnStatementSyntax singleReturn &&
                singleReturn.Expression != null)
            {
                return Substitute(singleReturn.Expression, parameterMap);
            }

            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"Cannot inline method '{_methodSymbol.Name}' as an expression; only a return-only or expression-bodied method is supported.");
        }

        private static T Substitute<T>(T node, IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
            where T : SyntaxNode
        {
            var rewriter = new ParameterSubstituter(parameterMap);
            return (T)rewriter.Visit(node)!;
        }

        private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression, SyntaxNode? parent)
        {
            if (expression is IdentifierNameSyntax or LiteralExpressionSyntax or ParenthesizedExpressionSyntax
                or InvocationExpressionSyntax or MemberAccessExpressionSyntax)
            {
                return expression;
            }

            if (parent is BinaryExpressionSyntax or MemberAccessExpressionSyntax or ConditionalExpressionSyntax
                or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or ElementAccessExpressionSyntax
                or AssignmentExpressionSyntax or CastExpressionSyntax or AwaitExpressionSyntax)
            {
                return SyntaxFactory.ParenthesizedExpression(expression);
            }

            return expression;
        }
    }

    private sealed class ParameterSubstituter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, ExpressionSyntax> _parameterMap;

        public ParameterSubstituter(IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
        {
            _parameterMap = parameterMap;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!_parameterMap.TryGetValue(node.Identifier.Text, out var replacement))
                return base.VisitIdentifierName(node);

            var needsParens = node.Parent is BinaryExpressionSyntax or MemberAccessExpressionSyntax
                or ConditionalExpressionSyntax or PrefixUnaryExpressionSyntax
                or ElementAccessExpressionSyntax or AssignmentExpressionSyntax or AwaitExpressionSyntax;

            if (needsParens && replacement is BinaryExpressionSyntax or ConditionalExpressionSyntax
                or AssignmentExpressionSyntax or PrefixUnaryExpressionSyntax)
            {
                return SyntaxFactory.ParenthesizedExpression(replacement.WithoutTrivia())
                    .WithTriviaFrom(node);
            }

            return replacement.WithoutTrivia().WithTriviaFrom(node);
        }
    }
}

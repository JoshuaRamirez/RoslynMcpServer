using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts a synchronous method to async/await pattern.
/// </summary>
public sealed class ConvertToAsyncOperation : RefactoringOperationBase<ConvertToAsyncParams>
{
    /// <summary>
    /// Creates a new convert to async operation.
    /// </summary>
    public ConvertToAsyncOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToAsyncParams @params)
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
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToAsyncParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find method declaration
        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == @params.MethodName)
            .ToList();

        if (methodDeclarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{@params.MethodName}' not found.");
        }

        MethodDeclarationSyntax methodDecl;
        if (methodDeclarations.Count > 1)
        {
            if (!@params.Line.HasValue)
            {
                var lines = methodDeclarations
                    .Select(m => m.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                    .ToList();
                throw new RefactoringException(
                    ErrorCodes.SymbolAmbiguous,
                    $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
            }

            methodDecl = methodDeclarations.FirstOrDefault(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value)
                ?? throw new RefactoringException(
                    ErrorCodes.MethodNotFound,
                    $"Method '{@params.MethodName}' not found at line {@params.Line}.");
        }
        else
        {
            methodDecl = methodDeclarations[0];
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");
        }

        // Check if already async
        if (methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            throw new RefactoringException(
                ErrorCodes.AlreadyAsync,
                "Method is already async.");
        }

        // Check for yield statements (iterators can't be async)
        if (methodDecl.DescendantNodes().OfType<YieldStatementSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertIterator,
                "Cannot convert iterator method to async.");
        }

        // Find awaitable calls in the method
        var awaitableCalls = FindAwaitableCalls(methodDecl, semanticModel, cancellationToken);

        if (awaitableCalls.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoAsyncCalls,
                "Method has no awaitable calls to convert.");
        }

        // Determine new method name
        var newMethodName = @params.RenameToAsync && !@params.MethodName.EndsWith("Async")
            ? @params.MethodName + "Async"
            : @params.MethodName;

        // Convert return type
        var newReturnType = SyntaxGenerationHelper.ToAsyncReturnType(methodSymbol.ReturnType);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, methodSymbol, newMethodName, awaitableCalls.Count);
        }

        // Build new method
        var newMethod = methodDecl
            .WithIdentifier(SyntaxFactory.Identifier(newMethodName))
            .WithReturnType(newReturnType.WithTrailingTrivia(SyntaxFactory.Space))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        // Add await to awaitable calls
        var rewriter = new AsyncRewriter(awaitableCalls, semanticModel);
        newMethod = (MethodDeclarationSyntax)rewriter.Visit(newMethod);

        // Replace in document
        var newRoot = root.ReplaceNode(methodDecl, newMethod);
        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // Update call sites if method was renamed
        if (newMethodName != @params.MethodName)
        {
            var references = await SymbolFinder.FindReferencesAsync(
                methodSymbol,
                newSolution,
                cancellationToken);

            foreach (var reference in references.SelectMany(r => r.Locations))
            {
                var refDoc = newSolution.GetDocument(reference.Document.Id);
                if (refDoc == null) continue;

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null) continue;

                var refNode = refRoot.FindNode(reference.Location.SourceSpan);
                if (refNode is IdentifierNameSyntax identifier &&
                    identifier.Identifier.Text == @params.MethodName)
                {
                    var newIdentifier = SyntaxFactory.IdentifierName(newMethodName)
                        .WithTriviaFrom(identifier);
                    var newRefRoot = refRoot.ReplaceNode(identifier, newIdentifier);
                    newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
                }
            }
        }

        // Commit changes
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
                Name = newMethodName,
                FullyQualifiedName = $"{methodSymbol.ContainingType.ToDisplayString()}.{newMethodName}",
                Kind = Contracts.Enums.SymbolKind.Method
            },
            awaitableCalls.Count,
            0);
    }

    private static List<InvocationExpressionSyntax> FindAwaitableCalls(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var awaitableCalls = new List<InvocationExpressionSyntax>();

        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            if (methodInfo.Symbol is not IMethodSymbol calledMethod) continue;

            // Check if return type is awaitable (Task, Task<T>, ValueTask, ValueTask<T>)
            var returnType = calledMethod.ReturnType;
            if (IsAwaitableType(returnType))
            {
                awaitableCalls.Add(invocation);
            }
        }

        return awaitableCalls;
    }

    private static bool IsAwaitableType(ITypeSymbol type)
    {
        var typeName = type.ToDisplayString();
        return typeName.StartsWith("System.Threading.Tasks.Task") ||
               typeName.StartsWith("System.Threading.Tasks.ValueTask") ||
               typeName.StartsWith("Task<") ||
               typeName.StartsWith("ValueTask<") ||
               typeName == "Task" ||
               typeName == "ValueTask";
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ConvertToAsyncParams @params,
        IMethodSymbol method,
        string newMethodName,
        int awaitableCallCount)
    {
        var oldSig = $"{method.ReturnType.ToDisplayString()} {@params.MethodName}(...)";
        var newReturnType = method.ReturnsVoid ? "Task" : $"Task<{method.ReturnType.ToDisplayString()}>";
        var newSig = $"async {newReturnType} {newMethodName}(...)";

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Convert '{@params.MethodName}' to async ({awaitableCallCount} await expressions added)",
                BeforeSnippet = oldSig,
                AfterSnippet = newSig
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed class AsyncRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<InvocationExpressionSyntax> _awaitableCalls;
        private readonly SemanticModel _semanticModel;

        public AsyncRewriter(List<InvocationExpressionSyntax> awaitableCalls, SemanticModel semanticModel)
        {
            _awaitableCalls = new HashSet<InvocationExpressionSyntax>(awaitableCalls);
            _semanticModel = semanticModel;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            // Check if this invocation should be awaited
            // We need to find the original node in our set
            if (_awaitableCalls.Any(c => c.Span == node.Span))
            {
                // Wrap in await expression
                return SyntaxFactory.AwaitExpression(visited)
                    .WithTriviaFrom(visited);
            }

            return visited;
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Adds null-check statements to method or constructor parameters.
/// </summary>
public sealed class AddNullChecksOperation : RefactoringOperationBase<AddNullChecksParams>
{
    /// <inheritdoc />
    public AddNullChecksOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AddNullChecksParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        AddNullChecksParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var useThrowIfNull = string.IsNullOrWhiteSpace(@params.Style) ||
                             string.Equals(@params.Style, "throw", StringComparison.OrdinalIgnoreCase);

        // Find the method or constructor
        var methodNode = FindMethod(root, @params.MethodName, @params.Line);
        if (methodNode == null)
            throw new RefactoringException(ErrorCodes.MethodNotFound, $"Method '{@params.MethodName}' not found.");

        // Get parameters from the method symbol
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        // Find nullable reference type parameters that need checks
        var paramsToCheck = methodSymbol.Parameters
            .Where(NullCheckGenerator.ShouldCheckForNull)
            .ToList();

        if (paramsToCheck.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No parameters require null checks.");

        // Generate null-check statements
        var nullChecks = new List<StatementSyntax>();
        foreach (var param in paramsToCheck)
        {
            var check = useThrowIfNull
                ? NullCheckGenerator.GenerateThrowIfNull(param.Name)
                : NullCheckGenerator.GenerateGuardClause(param.Name);
            nullChecks.Add(check);
        }

        if (@params.Preview)
        {
            var code = string.Join("\n", nullChecks.Select(s => s.NormalizeWhitespace().ToFullString()));
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = $"Add null checks to {@params.MethodName}",
                    BeforeSnippet = $"// Method '{@params.MethodName}' (no null checks)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        // Insert null checks at the beginning of the method body
        var body = GetBody(methodNode);
        if (body == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Method has no body to insert null checks into.");

        var newStatements = nullChecks.Concat(body.Statements);
        var newBody = body.WithStatements(SyntaxFactory.List(newStatements));
        var newMethodNode = ReplaceBody(methodNode, newBody);

        var newRoot = root.ReplaceNode(methodNode, newMethodNode);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = @params.MethodName, FullyQualifiedName = @params.MethodName, Kind = Contracts.Enums.SymbolKind.Method },
            0, 0);
    }

    private static SyntaxNode? FindMethod(SyntaxNode root, string methodName, int? line)
    {
        var candidates = root.DescendantNodes()
            .Where(n => n is MethodDeclarationSyntax m && m.Identifier.Text == methodName ||
                        n is ConstructorDeclarationSyntax c && c.Identifier.Text == methodName)
            .ToList();

        if (candidates.Count == 0)
            return null;

        if (line.HasValue)
        {
            return candidates.FirstOrDefault(c =>
            {
                var lineSpan = c.GetLocation().GetLineSpan();
                return lineSpan.StartLinePosition.Line + 1 == line.Value;
            }) ?? candidates.First();
        }

        return candidates.First();
    }

    private static BlockSyntax? GetBody(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Body,
        ConstructorDeclarationSyntax c => c.Body,
        _ => null
    };

    private static SyntaxNode ReplaceBody(SyntaxNode node, BlockSyntax newBody) => node switch
    {
        MethodDeclarationSyntax m => m.WithBody(newBody),
        ConstructorDeclarationSyntax c => c.WithBody(newBody),
        _ => node
    };
}

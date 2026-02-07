using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts foreach loops with Add/accumulate patterns to LINQ expressions.
/// </summary>
public sealed class ConvertForeachLinqOperation : RefactoringOperationBase<ConvertForeachLinqParams>
{
    /// <inheritdoc />
    public ConvertForeachLinqOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertForeachLinqParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertForeachLinqParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var targetLine = @params.Line - 1;

        // Find foreach at the specified line
        var foreachStmt = root.DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .FirstOrDefault(f => f.GetLocation().GetLineSpan().StartLinePosition.Line == targetLine);

        if (foreachStmt == null)
            throw new RefactoringException(ErrorCodes.CannotConvert, $"No foreach statement found at line {@params.Line}.");

        // Analyze the foreach body to determine conversion type
        var body = foreachStmt.Statement;
        var statements = body is BlockSyntax block ? block.Statements.ToList() : new List<StatementSyntax> { body };

        // Pattern: foreach(var x in collection) { list.Add(expr); }
        // → collection.Select(x => expr).ToList()
        if (TryConvertAddPattern(foreachStmt, statements, out var selectExpr, out var beforeSnippet))
        {
            if (@params.Preview)
            {
                var pendingChanges = new List<PendingChange>
                {
                    new()
                    {
                        File = @params.SourceFile,
                        ChangeType = ChangeKind.Modify,
                        Description = "Convert foreach with Add to LINQ Select",
                        BeforeSnippet = beforeSnippet!,
                        AfterSnippet = selectExpr!.NormalizeWhitespace().ToFullString()
                    }
                };
                return RefactoringResult.PreviewResult(operationId, pendingChanges);
            }

            // Find the list initialization and foreach, replace both
            var newRoot = ReplaceForeachWithLinq(root, foreachStmt, selectExpr!);
            var newDocument = document.WithSyntaxRoot(newRoot);
            var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

            return RefactoringResult.Succeeded(operationId,
                new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
                null, 0, 0);
        }

        // Pattern: foreach with if filter + Add
        // → collection.Where(x => condition).Select(x => expr).ToList()
        if (TryConvertFilterPattern(foreachStmt, statements, out var filterExpr, out beforeSnippet))
        {
            if (@params.Preview)
            {
                var pendingChanges = new List<PendingChange>
                {
                    new()
                    {
                        File = @params.SourceFile,
                        ChangeType = ChangeKind.Modify,
                        Description = "Convert foreach with filter to LINQ Where + Select",
                        BeforeSnippet = beforeSnippet!,
                        AfterSnippet = filterExpr!.NormalizeWhitespace().ToFullString()
                    }
                };
                return RefactoringResult.PreviewResult(operationId, pendingChanges);
            }

            var newRoot = ReplaceForeachWithLinq(root, foreachStmt, filterExpr!);
            var newDocument = document.WithSyntaxRoot(newRoot);
            var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

            return RefactoringResult.Succeeded(operationId,
                new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
                null, 0, 0);
        }

        throw new RefactoringException(ErrorCodes.CannotConvert,
            "Could not identify a convertible foreach pattern. Supported: foreach+Add, foreach+if+Add.");
    }

    private static bool TryConvertAddPattern(ForEachStatementSyntax foreach_, List<StatementSyntax> statements,
        out ExpressionSyntax? result, out string? before)
    {
        result = null;
        before = null;

        if (statements.Count != 1) return false;

        // Match: list.Add(expr)
        if (statements[0] is not ExpressionStatementSyntax exprStmt) return false;
        if (exprStmt.Expression is not InvocationExpressionSyntax invocation) return false;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
        if (memberAccess.Name.Identifier.Text != "Add") return false;
        if (invocation.ArgumentList.Arguments.Count != 1) return false;

        var addArg = invocation.ArgumentList.Arguments[0].Expression;
        var varName = foreach_.Identifier.Text;
        var collection = foreach_.Expression;
        var listName = memberAccess.Expression;

        // Build: listName = collection.Select(varName => addArg).ToList()
        var lambda = SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(varName)),
            addArg);

        var selectCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                collection,
                SyntaxFactory.IdentifierName("Select")))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(lambda))));

        var toListCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                selectCall,
                SyntaxFactory.IdentifierName("ToList")));

        result = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            listName,
            toListCall);

        before = foreach_.NormalizeWhitespace().ToFullString();
        return true;
    }

    private static bool TryConvertFilterPattern(ForEachStatementSyntax foreach_, List<StatementSyntax> statements,
        out ExpressionSyntax? result, out string? before)
    {
        result = null;
        before = null;

        if (statements.Count != 1) return false;
        if (statements[0] is not IfStatementSyntax ifStmt) return false;

        // Get inner statements from the if
        var innerStatements = ifStmt.Statement is BlockSyntax innerBlock
            ? innerBlock.Statements.ToList()
            : new List<StatementSyntax> { ifStmt.Statement };

        if (innerStatements.Count != 1) return false;
        if (innerStatements[0] is not ExpressionStatementSyntax exprStmt) return false;
        if (exprStmt.Expression is not InvocationExpressionSyntax invocation) return false;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
        if (memberAccess.Name.Identifier.Text != "Add") return false;
        if (invocation.ArgumentList.Arguments.Count != 1) return false;

        var addArg = invocation.ArgumentList.Arguments[0].Expression;
        var varName = foreach_.Identifier.Text;
        var collection = foreach_.Expression;
        var listName = memberAccess.Expression;
        var condition = ifStmt.Condition;

        // Build: listName = collection.Where(varName => condition).Select(varName => addArg).ToList()
        var whereLambda = SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(varName)),
            condition);

        var whereCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                collection,
                SyntaxFactory.IdentifierName("Where")))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(whereLambda))));

        var selectLambda = SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(varName)),
            addArg);

        var selectCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                whereCall,
                SyntaxFactory.IdentifierName("Select")))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(selectLambda))));

        var toListCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                selectCall,
                SyntaxFactory.IdentifierName("ToList")));

        result = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            listName,
            toListCall);

        before = foreach_.NormalizeWhitespace().ToFullString();
        return true;
    }

    private static SyntaxNode ReplaceForeachWithLinq(SyntaxNode root, ForEachStatementSyntax foreach_, ExpressionSyntax linqExpr)
    {
        var replacement = SyntaxFactory.ExpressionStatement(linqExpr)
            .WithLeadingTrivia(foreach_.GetLeadingTrivia())
            .WithTrailingTrivia(foreach_.GetTrailingTrivia());

        return root.ReplaceNode(foreach_, replacement);
    }
}

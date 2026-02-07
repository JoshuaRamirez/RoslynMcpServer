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
/// Converts members between expression body and block body forms.
/// </summary>
public sealed class ConvertExpressionBodyOperation : RefactoringOperationBase<ConvertExpressionBodyParams>
{
    /// <inheritdoc />
    public ConvertExpressionBodyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertExpressionBodyParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.MemberName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either memberName or line must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!Enum.TryParse<ConversionDirection>(@params.Direction, ignoreCase: true, out var dir) ||
            (dir != ConversionDirection.ToExpressionBody && dir != ConversionDirection.ToBlockBody))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "direction must be 'ToExpressionBody' or 'ToBlockBody'.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertExpressionBodyParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var direction = Enum.Parse<ConversionDirection>(@params.Direction, ignoreCase: true);

        // Find the target member
        var member = FindMember(root, @params.MemberName, @params.Line);
        if (member == null)
            throw new RefactoringException(ErrorCodes.MethodNotFound,
                $"Member '{@params.MemberName ?? $"at line {@params.Line}"}' not found.");

        SyntaxNode newMember;
        string beforeSnippet;
        string afterSnippet;

        if (direction == ConversionDirection.ToExpressionBody)
        {
            (newMember, beforeSnippet, afterSnippet) = ConvertToExpressionBody(member);
        }
        else
        {
            (newMember, beforeSnippet, afterSnippet) = ConvertToBlockBody(member);
        }

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Convert member to {direction}",
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newRoot = root.ReplaceNode(member, newMember);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            null, 0, 0);
    }

    private static MemberDeclarationSyntax? FindMember(SyntaxNode root, string? memberName, int? line)
    {
        var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
                        or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax);

        if (!string.IsNullOrWhiteSpace(memberName))
        {
            members = members.Where(m => GetMemberName(m) == memberName);
        }

        if (line.HasValue)
        {
            members = members.Where(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line.Value);
        }

        return members.FirstOrDefault();
    }

    private static string? GetMemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}",
        ConversionOperatorDeclarationSyntax c => $"implicit/explicit operator",
        _ => null
    };

    private static (SyntaxNode newNode, string before, string after) ConvertToExpressionBody(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                if (method.ExpressionBody != null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Method already has expression body.");
                if (method.Body == null || method.Body.Statements.Count != 1)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Method body must contain exactly one statement to convert.");

                var expr = ExtractExpression(method.Body.Statements[0]);
                if (expr == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Cannot extract expression from statement.");

                var before = method.Body.ToString().Trim();
                var newMethod = method
                    .WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .NormalizeWhitespace();
                return (newMethod, before, $"=> {expr.NormalizeWhitespace()};");

            case PropertyDeclarationSyntax prop:
                if (prop.ExpressionBody != null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Property already has expression body.");
                if (prop.AccessorList?.Accessors.Count != 1 || prop.AccessorList.Accessors[0].Keyword.IsKind(SyntaxKind.SetKeyword))
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Only single get-only properties can be converted to expression body.");

                var getter = prop.AccessorList!.Accessors[0];
                ExpressionSyntax? propExpr;

                if (getter.ExpressionBody != null)
                {
                    propExpr = getter.ExpressionBody.Expression;
                }
                else if (getter.Body?.Statements.Count == 1)
                {
                    propExpr = ExtractExpression(getter.Body.Statements[0]);
                }
                else
                {
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Getter must contain exactly one return statement.");
                }

                if (propExpr == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Cannot extract expression from getter.");

                var propBefore = prop.AccessorList.ToString().Trim();
                var newProp = prop
                    .WithAccessorList(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(propExpr))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .NormalizeWhitespace();
                return (newProp, propBefore, $"=> {propExpr.NormalizeWhitespace()};");

            default:
                throw new RefactoringException(ErrorCodes.CannotConvert, "Member type does not support expression body conversion.");
        }
    }

    private static (SyntaxNode newNode, string before, string after) ConvertToBlockBody(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                if (method.ExpressionBody == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Method does not have an expression body.");

                var isVoid = method.ReturnType is PredefinedTypeSyntax predefined &&
                             predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

                StatementSyntax stmt = isVoid
                    ? SyntaxFactory.ExpressionStatement(method.ExpressionBody.Expression)
                    : SyntaxFactory.ReturnStatement(method.ExpressionBody.Expression);

                var methodBefore = $"=> {method.ExpressionBody.Expression.NormalizeWhitespace()};";
                var newMethod = method
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(SyntaxFactory.Block(stmt))
                    .NormalizeWhitespace();
                return (newMethod, methodBefore, newMethod.Body!.ToString().Trim());

            case PropertyDeclarationSyntax prop:
                if (prop.ExpressionBody == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have an expression body.");

                var returnStmt = SyntaxFactory.ReturnStatement(prop.ExpressionBody.Expression);
                var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithBody(SyntaxFactory.Block(returnStmt));

                var propBefore = $"=> {prop.ExpressionBody.Expression.NormalizeWhitespace()};";
                var newProp = prop
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(accessor)))
                    .NormalizeWhitespace();
                return (newProp, propBefore, newProp.AccessorList!.ToString().Trim());

            default:
                throw new RefactoringException(ErrorCodes.CannotConvert, "Member type does not support block body conversion.");
        }
    }

    private static ExpressionSyntax? ExtractExpression(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax returnStmt => returnStmt.Expression,
        ExpressionStatementSyntax exprStmt => exprStmt.Expression,
        ThrowStatementSyntax throwStmt => throwStmt.Expression != null
            ? SyntaxFactory.ThrowExpression(throwStmt.Expression)
            : null,
        _ => null
    };
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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
    protected override void ValidateParams(ConvertExpressionBodyParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-expression-body parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertExpressionBodyParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");

        if (!Enum.TryParse<ConversionDirection>(@params.Direction, ignoreCase: true, out var dir) ||
            (dir != ConversionDirection.ToExpressionBody && dir != ConversionDirection.ToBlockBody))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "direction must be 'ToExpressionBody' or 'ToBlockBody'.");
        }

        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.MemberName) || @params.Line.HasValue || @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with memberName, line, or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.MemberName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either memberName or line must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertExpressionBodyParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var direction = Enum.Parse<ConversionDirection>(@params.Direction, ignoreCase: true);

        // Find the target member
        var member = FindMember(root, @params.MemberName, @params.Line, @params.Column);
        if (member == null)
        {
            var location = @params.Column.HasValue
                ? $"{@params.MemberName ?? $"at line {@params.Line}"}, column {@params.Column.Value}"
                : @params.MemberName ?? $"at line {@params.Line}";
            throw new RefactoringException(ErrorCodes.MethodNotFound,
                $"Member '{location}' not found.");
        }

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
                    File = @params.SourceFile!,
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

    /// <summary>
    /// Converts every eligible supported member in every C# document
    /// (same document filter as <c>AddBracesOperation.ExecuteAllFilesAsync</c> /
    /// <c>RemoveBracesOperation.ExecuteAllFilesAsync</c> /
    /// <c>SimplifyNameOperation.ExecuteAllFilesAsync</c>: <c>FilePath</c> ends
    /// with <c>.cs</c>). Already-in-target-form and otherwise ineligible
    /// members or documents are skipped. When every file is a no-op, succeeds
    /// with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ConvertExpressionBodyParams @params,
        CancellationToken cancellationToken)
    {
        var direction = Enum.Parse<ConversionDirection>(@params.Direction, ignoreCase: true);
        var currentSolution = Context.Solution;
        var allDocuments = currentSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDocument = currentSolution.GetDocument(document.Id) ?? document;
            if (currentDocument is SourceGeneratedDocument)
                continue;

            var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                continue;

            var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var member in CollectSupportedMembers(root))
            {
                if (TryConvert(member, direction, out var newMember, out _, out _))
                    replacements[member] = newMember;
            }

            if (replacements.Count == 0)
                continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            var newDocument = currentDocument.WithSyntaxRoot(newRoot);
            var beforeText = await currentDocument.GetTextAsync(cancellationToken);
            var afterText = await newDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var span = root.GetLocation().GetLineSpan();
                allPendingChanges.Add(new PendingChange
                {
                    File = currentDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = BuildAllFilesDescription(direction, replacements.Count),
                    BeforeSnippet = root.NormalizeWhitespace().ToFullString().Trim(),
                    AfterSnippet = newRoot.NormalizeWhitespace().ToFullString().Trim(),
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                });
                continue;
            }

            currentSolution = newDocument.Project.Solution;
            anyChanged = true;
        }

        if (@params.Preview)
            return RefactoringResult.PreviewResult(operationId, allPendingChanges);

        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
            return RefactoringResult.Succeeded(operationId,
                new FileChanges
                {
                    FilesModified = commitResult.FilesModified,
                    FilesCreated = commitResult.FilesCreated,
                    FilesDeleted = commitResult.FilesDeleted
                },
                null, 0, 0);
        }

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
            null, 0, 0);
    }

    internal static string BuildAllFilesDescription(ConversionDirection direction, int convertedCount) =>
        convertedCount == 1
            ? $"Convert member to {direction}"
            : $"Convert {convertedCount} members to {direction}";

    /// <summary>
    /// Finds a convertible member. When <paramref name="column"/> is omitted,
    /// keeps today's first-match (memberName and/or line). When set, picks
    /// the member whose identifier or declaration span covers that 1-based
    /// column.
    /// </summary>
    internal static MemberDeclarationSyntax? FindMember(
        SyntaxNode root,
        string? memberName,
        int? line,
        int? column)
    {
        var members = CollectSupportedMembers(root);

        if (!string.IsNullOrWhiteSpace(memberName))
        {
            members = members.Where(m => GetMemberName(m) == memberName);
        }

        // Omitted column keeps today's start-line filter (first match on the
        // line). When column is set, do not require the declaration to start
        // on `line` — a split signature's identifier may live on a
        // continuation line whose declaration span still covers that column.
        if (line.HasValue && !column.HasValue)
        {
            members = members.Where(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line.Value);
        }

        if (!column.HasValue)
            return members.FirstOrDefault();

        var atColumn = members
            .Where(m => MemberCoversColumn(m, line ?? StartLine(m), column.Value))
            .OrderBy(m => IdentifierCoversColumn(m, line ?? StartLine(m), column.Value) ? 0 : 1)
            .ThenBy(m => m.Span.Length)
            .ToList();
        return atColumn.FirstOrDefault();
    }

    private static int StartLine(MemberDeclarationSyntax member) =>
        member.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool MemberCoversColumn(MemberDeclarationSyntax member, int line, int column) =>
        IdentifierCoversColumn(member, line, column) ||
        SpanCoversColumn(member.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(MemberDeclarationSyntax member, int line, int column)
    {
        var token = GetIdentifierToken(member);
        return token != default && SpanCoversColumn(token.GetLocation().GetLineSpan(), line, column);
    }

    private static SyntaxToken GetIdentifierToken(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
        OperatorDeclarationSyntax op => op.OperatorToken,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetFirstToken(),
        _ => default
    };

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent member also
    /// match the previous declaration.
    /// </summary>
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
        if (line == endLine && column >= endCol)
            return false;
        return true;
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

    private static IEnumerable<MemberDeclarationSyntax> CollectSupportedMembers(SyntaxNode root) =>
        root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
                        or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax);

    /// <summary>
    /// Attempts a conversion without throwing. Used by allFiles so already-
    /// converted and otherwise ineligible members stay no-ops.
    /// </summary>
    internal static bool TryConvert(
        MemberDeclarationSyntax member,
        ConversionDirection direction,
        out SyntaxNode newMember,
        out string beforeSnippet,
        out string afterSnippet)
    {
        try
        {
            (newMember, beforeSnippet, afterSnippet) = direction == ConversionDirection.ToExpressionBody
                ? ConvertToExpressionBody(member)
                : ConvertToBlockBody(member);
            return true;
        }
        catch (RefactoringException)
        {
            newMember = member;
            beforeSnippet = string.Empty;
            afterSnippet = string.Empty;
            return false;
        }
    }

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

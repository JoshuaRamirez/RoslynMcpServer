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
/// LINQ conversion shape detected from a foreach body (UC-CV2).
/// </summary>
internal enum LinqConversionKind
{
    /// <summary>Projection: <c>list.Add(f(x))</c> → Select + ToList.</summary>
    Project,

    /// <summary>Filter: <c>if (p(x)) list.Add(x)</c> → Where + ToList.</summary>
    Filter,

    /// <summary>Filter + project: <c>if (p(x)) list.Add(f(x))</c> → Where + Select + ToList.</summary>
    FilterAndProject,

    /// <summary>Existence: <c>if (p(x)) { found = true; break; }</c> → Any. No query-syntax form.</summary>
    Any,

    /// <summary>Universal: <c>if (!p(x)) { all = false; break; }</c> → All. No query-syntax form.</summary>
    All,

    /// <summary>First match: <c>if (p(x)) { result = x; break; }</c> → FirstOrDefault. No query-syntax form.</summary>
    FirstOrDefault,

    /// <summary>Count: <c>if (p(x)) count++</c> → Count. No query-syntax form.</summary>
    Count,

    /// <summary>Sum: <c>sum += x.Value</c> → Sum. No query-syntax form.</summary>
    Sum
}

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
    protected override void ValidateParams(ConvertForeachLinqParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-foreach-linq parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertForeachLinqParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

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

        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var foreachStmt = FindForeachStatement(root, @params.Line, @params.Column);
        if (foreachStmt == null)
        {
            var location = @params.Column.HasValue
                ? $"line {@params.Line}, column {@params.Column.Value}"
                : $"line {@params.Line}";
            throw new RefactoringException(ErrorCodes.CannotConvert, $"No foreach statement found at {location}.");
        }

        var body = foreachStmt.Statement;
        var statements = body is BlockSyntax block ? block.Statements.ToList() : new List<StatementSyntax> { body };

        if (!TryAnalyzeAddPattern(foreachStmt, statements, out var conversion) &&
            !TryAnalyzeFilterPattern(foreachStmt, statements, out conversion))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "Could not identify a convertible foreach pattern. Supported: foreach+Add, foreach+if+Add.");
        }

        var assignment = BuildLinqAssignment(conversion!, @params.PreferQuerySyntax);
        var description = DescribeRewrite(conversion!.Kind, @params.PreferQuerySyntax);

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = description,
                    BeforeSnippet = conversion.BeforeSnippet,
                    AfterSnippet = assignment.NormalizeWhitespace().ToFullString()
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newRoot = ReplaceForeachWithLinq(root, foreachStmt, assignment);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            null, 0, 0);
    }

    /// <summary>
    /// Finds the foreach on <paramref name="line"/>. When
    /// <paramref name="column"/> is set, picks the statement whose
    /// <c>foreach</c> keyword covers that column.
    /// </summary>
    internal static ForEachStatementSyntax? FindForeachStatement(SyntaxNode root, int line, int? column)
    {
        var onLine = root.DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .Where(statement => KeywordIsOnLine(statement, line))
            .ToList();

        if (onLine.Count == 0)
            return null;

        if (column.HasValue)
        {
            var atColumn = onLine
                .Where(statement => KeywordCoversColumn(statement, line, column.Value))
                .OrderBy(statement => statement.ForEachKeyword.Span.Length)
                .ToList();
            return atColumn.FirstOrDefault();
        }

        return onLine.OrderBy(statement => statement.ForEachKeyword.SpanStart).First();
    }

    /// <summary>
    /// Query syntax exists for filter / project / filter+project (and ToList
    /// after that query). Any / All / FirstOrDefault / Count / Sum have no
    /// query-syntax form — keep method syntax rather than inventing invalid
    /// query syntax.
    /// </summary>
    internal static bool HasQuerySyntaxForm(LinqConversionKind kind) =>
        kind is LinqConversionKind.Project
            or LinqConversionKind.Filter
            or LinqConversionKind.FilterAndProject;

    /// <summary>
    /// Preview / change description for the rewrite.
    /// </summary>
    internal static string DescribeRewrite(LinqConversionKind kind, bool preferQuerySyntax)
    {
        var useQuery = preferQuerySyntax && HasQuerySyntaxForm(kind);
        return (kind, useQuery) switch
        {
            (LinqConversionKind.Project, false) => "Convert foreach with Add to LINQ Select",
            (LinqConversionKind.Project, true) => "Convert foreach with Add to LINQ query syntax (from … select)",
            (LinqConversionKind.Filter or LinqConversionKind.FilterAndProject, false) =>
                "Convert foreach with filter to LINQ Where + Select",
            (LinqConversionKind.Filter or LinqConversionKind.FilterAndProject, true) =>
                "Convert foreach with filter to LINQ query syntax (from … where … select)",
            _ => "Convert foreach to LINQ method syntax"
        };
    }

    /// <summary>
    /// Builds the assignment that replaces the foreach. When
    /// <paramref name="preferQuerySyntax"/> is true and the kind has a
    /// query-syntax form, emits <c>from … where … select</c> plus
    /// <c>.ToList()</c>. Aggregation kinds stay on method syntax.
    /// </summary>
    internal static ExpressionSyntax BuildLinqAssignment(ForeachLinqConversion conversion, bool preferQuerySyntax)
    {
        // Query keywords need elastic trivia expanded (from item, not fromitem).
        // Method syntax is unchanged so existing .Where().Select().ToList() stays as today.
        var linq = preferQuerySyntax && HasQuerySyntaxForm(conversion.Kind)
            ? BuildQueryThenToList(conversion).NormalizeWhitespace()
            : BuildMethodThenToList(conversion);

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            conversion.ListName,
            linq);
    }

    private static bool TryAnalyzeAddPattern(
        ForEachStatementSyntax foreach_,
        List<StatementSyntax> statements,
        out ForeachLinqConversion? conversion)
    {
        conversion = null;

        if (statements.Count != 1) return false;
        if (!TryMatchAdd(statements[0], out var listName, out var addArg)) return false;

        conversion = new ForeachLinqConversion(
            LinqConversionKind.Project,
            listName!,
            foreach_.Expression,
            foreach_.Identifier.Text,
            ExplicitElementType(foreach_.Type),
            Filter: null,
            addArg!,
            foreach_.NormalizeWhitespace().ToFullString());
        return true;
    }

    private static bool TryAnalyzeFilterPattern(
        ForEachStatementSyntax foreach_,
        List<StatementSyntax> statements,
        out ForeachLinqConversion? conversion)
    {
        conversion = null;

        if (statements.Count != 1) return false;
        if (statements[0] is not IfStatementSyntax ifStmt) return false;

        var innerStatements = ifStmt.Statement is BlockSyntax innerBlock
            ? innerBlock.Statements.ToList()
            : new List<StatementSyntax> { ifStmt.Statement };

        if (innerStatements.Count != 1) return false;
        if (!TryMatchAdd(innerStatements[0], out var listName, out var addArg)) return false;

        conversion = new ForeachLinqConversion(
            LinqConversionKind.FilterAndProject,
            listName!,
            foreach_.Expression,
            foreach_.Identifier.Text,
            ExplicitElementType(foreach_.Type),
            ifStmt.Condition,
            addArg!,
            foreach_.NormalizeWhitespace().ToFullString());
        return true;
    }

    private static bool TryMatchAdd(StatementSyntax statement, out ExpressionSyntax? listName, out ExpressionSyntax? addArg)
    {
        listName = null;
        addArg = null;

        if (statement is not ExpressionStatementSyntax exprStmt) return false;
        if (exprStmt.Expression is not InvocationExpressionSyntax invocation) return false;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
        if (memberAccess.Name.Identifier.Text != "Add") return false;
        if (invocation.ArgumentList.Arguments.Count != 1) return false;

        listName = memberAccess.Expression;
        addArg = invocation.ArgumentList.Arguments[0].Expression;
        return true;
    }

    private static ExpressionSyntax BuildMethodThenToList(ForeachLinqConversion conversion)
    {
        ExpressionSyntax source = conversion.Collection;

        if (conversion.Filter != null)
        {
            var whereLambda = SyntaxFactory.SimpleLambdaExpression(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(conversion.VariableName)),
                conversion.Filter);

            source = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        source,
                        SyntaxFactory.IdentifierName("Where")))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(whereLambda))));
        }

        var selectLambda = SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(conversion.VariableName)),
            conversion.Projection);

        var selectCall = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    source,
                    SyntaxFactory.IdentifierName("Select")))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(selectLambda))));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                selectCall,
                SyntaxFactory.IdentifierName("ToList")));
    }

    private static ExpressionSyntax BuildQueryThenToList(ForeachLinqConversion conversion)
    {
        QueryBodySyntax body = SyntaxFactory.QueryBody(
            SyntaxFactory.SelectClause(conversion.Projection));

        if (conversion.Filter != null)
        {
            body = body.WithClauses(
                SyntaxFactory.SingletonList<QueryClauseSyntax>(
                    SyntaxFactory.WhereClause(conversion.Filter)));
        }

        var query = SyntaxFactory.QueryExpression(
            BuildFromClause(conversion),
            body);

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParenthesizedExpression(query),
                SyntaxFactory.IdentifierName("ToList")));
    }

    /// <summary>
    /// Explicit foreach element type, or null when the loop is <c>var</c>
    /// (query syntax stays untyped — <c>from item in …</c>, not
    /// <c>from var item in …</c>).
    /// </summary>
    internal static TypeSyntax? ExplicitElementType(TypeSyntax type) =>
        type.IsVar ? null : type.WithoutTrivia();

    private static FromClauseSyntax BuildFromClause(ForeachLinqConversion conversion)
    {
        var identifier = SyntaxFactory.Identifier(conversion.VariableName);
        if (conversion.ElementType != null)
            return SyntaxFactory.FromClause(conversion.ElementType, identifier, conversion.Collection);

        return SyntaxFactory.FromClause(identifier, conversion.Collection);
    }

    private static SyntaxNode ReplaceForeachWithLinq(SyntaxNode root, ForEachStatementSyntax foreach_, ExpressionSyntax linqExpr)
    {
        var replacement = SyntaxFactory.ExpressionStatement(linqExpr)
            .WithLeadingTrivia(foreach_.GetLeadingTrivia())
            .WithTrailingTrivia(foreach_.GetTrailingTrivia());

        return root.ReplaceNode(foreach_, replacement);
    }

    private static bool KeywordIsOnLine(ForEachStatementSyntax statement, int line)
    {
        var span = statement.ForEachKeyword.GetLocation().GetLineSpan();
        return span.StartLinePosition.Line + 1 == line;
    }

    private static bool KeywordCoversColumn(ForEachStatementSyntax statement, int line, int column)
    {
        return SpanCoversColumn(statement.ForEachKeyword.GetLocation().GetLineSpan(), line, column);
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
}

/// <summary>
/// Analyzed foreach → LINQ conversion.
/// </summary>
internal sealed record ForeachLinqConversion(
    LinqConversionKind Kind,
    ExpressionSyntax ListName,
    ExpressionSyntax Collection,
    string VariableName,
    TypeSyntax? ElementType,
    ExpressionSyntax? Filter,
    ExpressionSyntax Projection,
    string BeforeSnippet);

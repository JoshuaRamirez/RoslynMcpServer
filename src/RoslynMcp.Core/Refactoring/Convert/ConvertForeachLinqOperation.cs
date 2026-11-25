using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
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
    internal static void Validate(ConvertForeachLinqParams @params) =>
        LineSelectionParamsHelpers.ValidateAllFilesOrLine(
            @params.AllFiles,
            @params.SourceFile,
            @params.Line,
            @params.Column);

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertForeachLinqParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var foreachStmt = FindForeachStatement(root, @params.Line!.Value, @params.Column);
        if (foreachStmt == null)
        {
            var location = @params.Column.HasValue
                ? $"line {@params.Line}, column {@params.Column.Value}"
                : $"line {@params.Line}";
            throw new RefactoringException(ErrorCodes.CannotConvert, $"No foreach statement found at {location}.");
        }

        if (StatementContainsPreprocessorDirectives(foreachStmt))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "foreach body contains preprocessor directives and cannot be safely converted to LINQ.");
        }

        if (TryAnalyzePattern(foreachStmt, out var analyzed) &&
            AccumulatorHasDirectiveGuardedCodeBeforeLoop(foreachStmt, analyzed!.ListName))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "Preprocessor directives (#if / #elif / #else / #endif) guard code between the accumulator's " +
                "declaration and the foreach; the LINQ assignment would discard writes made there in some " +
                "build configuration, so the loop cannot be safely converted.");
        }

        if (!TryConvert(foreachStmt, @params.PreferQuerySyntax, out var conversion, out var assignment))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "Could not identify a convertible foreach pattern. Supported: foreach+Add, foreach+if+Add.");
        }

        var description = DescribeRewrite(conversion.Kind, @params.PreferQuerySyntax);

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile!,
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
    /// Converts every distinct eligible foreach in every C# document (same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c> /
    /// <c>ConvertToInterpolatedStringOperation.ExecuteAllFilesAsync</c> /
    /// <c>InvertIfOperation.ExecuteAllFilesAsync</c> /
    /// <c>ConvertExpressionBodyOperation.ExecuteAllFilesAsync</c> /
    /// <c>RemoveBracesOperation.ExecuteAllFilesAsync</c>:
    /// <c>FilePath</c> ends with <c>.cs</c>). Optional <c>sourceFile</c>
    /// limits the walk via
    /// <see cref="AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile"/>
    /// (default path; same as FormatDocument / SortUsings / AddMissingUsings /
    /// RemoveUnusedUsings / simplify_name / add_braces / remove_braces /
    /// convert_expression_body / invert_if). Foreach statements that cannot
    /// convert and documents whose text is unchanged are skipped. When every
    /// file is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ConvertForeachLinqParams @params,
        CancellationToken cancellationToken)
    {
        var currentSolution = Context.Solution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(currentSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            allDocuments = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);
        }

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

            var replacements = CollectConvertibleReplacements(root, @params.PreferQuerySyntax);

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
                    Description = BuildAllFilesDescription(replacements.Count),
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

    internal static string BuildAllFilesDescription(int convertedCount) =>
        convertedCount == 1
            ? "Convert foreach to LINQ"
            : $"Convert {convertedCount} foreach loops to LINQ";

    /// <summary>
    /// Collects every distinct eligible foreach in <paramref name="root"/>
    /// using the existing conversion helpers (not first-on-a-line). Nested
    /// convertible foreach keep the innermost so <c>ReplaceNodes</c> does
    /// not rewrite an ancestor and descendant together. Each foreach is
    /// analyzed once.
    /// </summary>
    internal static IReadOnlyList<ForEachStatementSyntax> CollectConvertibleForeach(
        SyntaxNode root,
        bool preferQuerySyntax) =>
        CollectConvertibleReplacements(root, preferQuerySyntax).Keys.ToList();

    /// <summary>
    /// Builds replacements for every distinct eligible foreach. Used by
    /// allFiles so <see cref="TryConvert"/> runs once per statement.
    /// </summary>
    internal static IReadOnlyDictionary<ForEachStatementSyntax, SyntaxNode> CollectConvertibleReplacements(
        SyntaxNode root,
        bool preferQuerySyntax)
    {
        var replacements = new Dictionary<ForEachStatementSyntax, SyntaxNode>();
        foreach (var statement in root.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (TryConvert(statement, preferQuerySyntax, out _, out var assignment))
                replacements[statement] = ReplaceForeachWithLinqStatement(statement, assignment);
        }

        return replacements
            .Where(pair => !replacements.Keys.Any(other => other != pair.Key && pair.Key.Contains(other)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>
    /// Attempts a conversion without throwing. Used by allFiles so foreach
    /// that cannot convert stay no-ops.
    /// </summary>
    internal static bool TryConvert(
        ForEachStatementSyntax foreachStmt,
        bool preferQuerySyntax,
        out ForeachLinqConversion conversion,
        out ExpressionSyntax assignment)
    {
        conversion = null!;
        assignment = null!;

        // Directives in the body (or disabled text from an inactive #if branch)
        // would be dropped by the rewrite, which only keeps the active branch
        // and the foreach statement's own leading/trailing trivia. Leading
        // trivia such as a loop wrapped in #if DEBUG is preserved and must
        // not block conversion.
        if (StatementContainsPreprocessorDirectives(foreachStmt))
            return false;

        if (!TryAnalyzePattern(foreachStmt, out var analyzed))
            return false;

        // The rewrite assigns over the accumulator, so writes between its
        // declaration and the loop are lost. Directive-guarded code there
        // (inactive text or an active #if branch) may write to it in some
        // build configuration, so refuse rather than discard it (#3476).
        if (AccumulatorHasDirectiveGuardedCodeBeforeLoop(foreachStmt, analyzed!.ListName))
            return false;

        conversion = analyzed;
        assignment = BuildLinqAssignment(conversion, preferQuerySyntax);
        return true;
    }

    private static bool TryAnalyzePattern(ForEachStatementSyntax foreachStmt, out ForeachLinqConversion? analyzed)
    {
        var body = foreachStmt.Statement;
        var statements = body is BlockSyntax block ? block.Statements.ToList() : new List<StatementSyntax> { body };

        return TryAnalyzeAddPattern(foreachStmt, statements, out analyzed) ||
               TryAnalyzeFilterPattern(foreachStmt, statements, out analyzed);
    }

    /// <summary>
    /// True when the accumulator is a local and the text between the end of
    /// its declaration and the <c>foreach</c> keyword contains disabled text
    /// (an inactive <c>#if</c> branch) or a conditional directive other than
    /// an <c>#if</c> that opens in the foreach's own leading trivia (a loop
    /// wrapped as a whole in <c>#if</c> still converts). Such code can write
    /// to the accumulator in some build configuration, and the LINQ
    /// assignment would silently discard those writes (#3476).
    /// </summary>
    internal static bool AccumulatorHasDirectiveGuardedCodeBeforeLoop(
        ForEachStatementSyntax foreachStmt,
        ExpressionSyntax accumulator)
    {
        var scanStart = FindAccumulatorScanStart(foreachStmt, accumulator);
        if (scanStart == null)
            return false;

        var start = scanStart.Value;
        var end = foreachStmt.SpanStart;
        if (end <= start)
            return false;

        var foreachLeading = foreachStmt.GetLeadingTrivia();
        var root = foreachStmt.SyntaxTree.GetRoot();
        foreach (var trivia in root.DescendantTrivia(TextSpan.FromBounds(start, end), descendIntoTrivia: true))
        {
            if (trivia.SpanStart < start || trivia.Span.End > end)
                continue;

            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                return true;

            if (trivia.IsKind(SyntaxKind.ElifDirectiveTrivia) ||
                trivia.IsKind(SyntaxKind.ElseDirectiveTrivia) ||
                trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia))
            {
                return true;
            }

            if (trivia.IsKind(SyntaxKind.IfDirectiveTrivia) && !foreachLeading.Contains(trivia))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Position from which to scan for directive-guarded code when the
    /// simple-name accumulator is a local, or null when it is not a local
    /// (field, parameter, member access). The local is resolved to its
    /// declaring syntax in the nearest enclosing body (method, accessor,
    /// local function, lambda or top-level statements): a local declaration
    /// statement, <c>using</c> / <c>for</c> / <c>fixed</c> declarator,
    /// <c>out var</c> or pattern / deconstruction designation, enclosing
    /// <c>foreach</c> variable or <c>catch</c> variable. The scan starts at
    /// the end of the statement containing that declaration, or at the end
    /// of the declaration itself when that statement also contains the loop
    /// (<c>if (Make(out var r)) { … foreach … }</c>). A local declared in an
    /// outer body and captured by the loop's lambda / local function has no
    /// anchor statement there, so the scan starts at the start of the
    /// loop's enclosing body.
    /// </summary>
    private static int? FindAccumulatorScanStart(
        ForEachStatementSyntax foreachStmt,
        ExpressionSyntax accumulator)
    {
        if (accumulator is not IdentifierNameSyntax identifier)
            return null;

        var name = identifier.Identifier.ValueText;
        var nearestBody = EnclosingBody(foreachStmt);
        if (nearestBody == null)
            return null;

        for (var body = nearestBody; body != null; body = EnclosingBody(body))
        {
            if (DeclaresParameter(body, name))
                return null;

            var declaration = FindLocalDeclaringSyntax(body, name, foreachStmt);
            if (declaration == null)
            {
                if (body is MemberDeclarationSyntax or AccessorDeclarationSyntax or CompilationUnitSyntax)
                    return null;
                continue;
            }

            if (body != nearestBody)
                return BodyStart(nearestBody);

            var statement = declaration.FirstAncestorOrSelf<StatementSyntax>();
            return statement != null && !statement.Span.Contains(foreachStmt.Span)
                ? statement.Span.End
                : declaration.Span.End;
        }

        return null;
    }

    private static bool IsBodyBoundary(SyntaxNode node) =>
        node is (MemberDeclarationSyntax and not GlobalStatementSyntax) or LocalFunctionStatementSyntax or
            AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax or CompilationUnitSyntax;

    private static SyntaxNode? EnclosingBody(SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (IsBodyBoundary(current))
                return current is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax ? null : current;
        }

        return null;
    }

    private static bool DeclaresParameter(SyntaxNode body, string name)
    {
        var parameters = body switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
            LocalFunctionStatementSyntax localFunction => localFunction.ParameterList.Parameters,
            ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters,
            SimpleLambdaExpressionSyntax lambda => SyntaxFactory.SingletonSeparatedList(lambda.Parameter),
            AnonymousMethodExpressionSyntax { ParameterList: { } list } => list.Parameters,
            IndexerDeclarationSyntax indexer => indexer.ParameterList.Parameters,
            _ => default
        };

        if (parameters.Any(parameter => parameter.Identifier.ValueText == name))
            return true;

        // Accessors see the indexer's parameters and set / init / add /
        // remove accessors see the implicit 'value' parameter.
        if (body is AccessorDeclarationSyntax accessor)
        {
            if (name == "value" && !accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                return true;
            if (accessor.Parent?.Parent is IndexerDeclarationSyntax indexer)
                return indexer.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == name);
        }

        return false;
    }

    /// <summary>
    /// Nearest local-declaring syntax named <paramref name="name"/> that
    /// ends before <paramref name="target"/> in <paramref name="body"/> and
    /// whose scope contains <paramref name="target"/>, not descending into
    /// nested bodies. Sibling-scope locals with the same name are ignored,
    /// so a shadowing local in an earlier block can't move the scan start
    /// past a guarded write to the outer accumulator.
    /// </summary>
    private static SyntaxNode? FindLocalDeclaringSyntax(SyntaxNode body, string name, SyntaxNode target)
    {
        var before = target.SpanStart;
        SyntaxNode? match = null;
        foreach (var node in body.DescendantNodes(child => child == body || !IsBodyBoundary(child)))
        {
            if (node == body || node.SpanStart >= before)
                continue;

            var declared = node switch
            {
                VariableDeclaratorSyntax declarator => declarator.Identifier,
                SingleVariableDesignationSyntax designation => designation.Identifier,
                ForEachStatementSyntax loop => loop.Identifier,
                CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier,
                _ => default
            };

            if (declared.ValueText != name || declared.Span.End > before)
                continue;

            if (!DeclarationScope(node, body).Span.Contains(target.Span))
                continue;

            if (match == null || declared.SpanStart > DeclaredTokenStart(match))
                match = node;
        }

        return match;
    }

    /// <summary>
    /// Innermost node that bounds the scope of the local declared by
    /// <paramref name="declaring"/>: its block, switch section, owning
    /// for / foreach / using / fixed / while / catch, or an embedded
    /// statement. This may under-estimate a scope (e.g. a pattern variable
    /// in a while condition), which only makes the check fall back to an
    /// earlier scan start and over-refuse, never under-refuse.
    /// </summary>
    private static SyntaxNode DeclarationScope(SyntaxNode declaring, SyntaxNode body)
    {
        for (var current = declaring; current != null && current != body; current = current.Parent)
        {
            switch (current)
            {
                case BlockSyntax or SwitchSectionSyntax or CatchClauseSyntax or
                    ForStatementSyntax or CommonForEachStatementSyntax or UsingStatementSyntax or
                    FixedStatementSyntax or WhileStatementSyntax:
                    return current;
                case StatementSyntax statement when statement.Parent is not (BlockSyntax or SwitchSectionSyntax or GlobalStatementSyntax):
                    return current;
            }
        }

        return body;
    }

    private static int DeclaredTokenStart(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax declarator => declarator.Identifier.SpanStart,
        SingleVariableDesignationSyntax designation => designation.Identifier.SpanStart,
        ForEachStatementSyntax loop => loop.Identifier.SpanStart,
        CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.SpanStart,
        _ => node.SpanStart
    };

    private static int BodyStart(SyntaxNode body) => body switch
    {
        BaseMethodDeclarationSyntax { Body: { } block } => block.SpanStart,
        BaseMethodDeclarationSyntax { ExpressionBody: { } arrow } => arrow.SpanStart,
        LocalFunctionStatementSyntax { Body: { } block } => block.SpanStart,
        LocalFunctionStatementSyntax { ExpressionBody: { } arrow } => arrow.SpanStart,
        AccessorDeclarationSyntax { Body: { } block } => block.SpanStart,
        AccessorDeclarationSyntax { ExpressionBody: { } arrow } => arrow.SpanStart,
        AnonymousFunctionExpressionSyntax lambda => lambda.Body.SpanStart,
        CompilationUnitSyntax unit when unit.Members.OfType<GlobalStatementSyntax>().FirstOrDefault() is { } first =>
            first.SpanStart,
        _ => body.SpanStart
    };

    /// <summary>
    /// Finds the foreach on <paramref name="line"/>. When
    /// <paramref name="column"/> is set, picks the statement whose
    /// <c>foreach</c> keyword covers that column.
    /// </summary>
    internal static ForEachStatementSyntax? FindForeachStatement(SyntaxNode root, int line, int? column)
    {
        var onLine = root.DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .Where(statement => KeywordCoverage.KeywordIsOnLine(statement.ForEachKeyword, line))
            .ToList();

        if (onLine.Count == 0)
            return null;

        if (column.HasValue)
        {
            var atColumn = onLine
                .Where(statement => KeywordCoverage.KeywordCoversColumn(statement.ForEachKeyword, line, column.Value))
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

    private static SyntaxNode ReplaceForeachWithLinq(SyntaxNode root, ForEachStatementSyntax foreach_, ExpressionSyntax linqExpr) =>
        root.ReplaceNode(foreach_, ReplaceForeachWithLinqStatement(foreach_, linqExpr));

    private static ExpressionStatementSyntax ReplaceForeachWithLinqStatement(
        ForEachStatementSyntax foreach_,
        ExpressionSyntax linqExpr) =>
        SyntaxFactory.ExpressionStatement(linqExpr)
            .WithLeadingTrivia(foreach_.GetLeadingTrivia())
            .WithTrailingTrivia(foreach_.GetTrailingTrivia());

    /// <summary>
    /// True when the foreach statement (its header, collection expression or
    /// body) contains preprocessor directives or disabled text. The foreach
    /// statement's own leading and trailing trivia are ignored because
    /// <see cref="ReplaceForeachWithLinqStatement"/> keeps them; a directive
    /// anywhere between the <c>foreach</c> keyword and the end of the body
    /// would be dropped or left unbalanced by the rewrite (#2916).
    /// </summary>
    internal static bool StatementContainsPreprocessorDirectives(ForEachStatementSyntax foreachStmt)
    {
        var inner = foreachStmt.Span;
        return foreachStmt.DescendantTrivia(descendIntoTrivia: true)
            .Any(trivia => (trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia)) &&
                           trivia.SpanStart >= inner.Start &&
                           trivia.Span.End <= inner.End);
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

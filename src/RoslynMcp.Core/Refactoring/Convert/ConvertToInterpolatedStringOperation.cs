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
/// Converts string.Format() calls and string concatenation to interpolated strings.
/// </summary>
public sealed class ConvertToInterpolatedStringOperation : RefactoringOperationBase<ConvertToInterpolatedStringParams>
{
    /// <inheritdoc />
    public ConvertToInterpolatedStringOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToInterpolatedStringParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-to-interpolated-string parameters. Internal so tests
    /// can exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertToInterpolatedStringParams @params)
    {
        if (@params.AllFiles)
        {
            if (@params.Line.HasValue || @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with line or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!@params.Line.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "line is required.");

        if (@params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToInterpolatedStringParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var target = FindConvertibleExpression(root, semanticModel, @params.Line!.Value, @params.Column);

        if (target is InvocationExpressionSyntax formatInvocation)
        {
            return await ConvertStringFormat(operationId, document, root, formatInvocation, @params, cancellationToken);
        }

        if (target is BinaryExpressionSyntax concat)
        {
            return await ConvertConcatenation(operationId, document, root, concat, @params, cancellationToken);
        }

        var location = @params.Column.HasValue
            ? $"line {@params.Line}, column {@params.Column.Value}"
            : $"line {@params.Line}";
        throw new RefactoringException(ErrorCodes.CannotConvert,
            $"No string.Format() call or string concatenation found at {location}.");
    }

    /// <summary>
    /// Converts every distinct convertible <c>string.Format</c> invocation and
    /// outer concatenation in every C# document (same document filter as
    /// <c>FormatDocumentOperation.ExecuteAllFilesAsync</c> /
    /// <c>ConvertExpressionBodyOperation.ExecuteAllFilesAsync</c>:
    /// <c>FilePath</c> ends with <c>.cs</c>). Expressions that cannot convert
    /// and documents whose text is unchanged are skipped. When every file is
    /// a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ConvertToInterpolatedStringParams @params,
        CancellationToken cancellationToken)
    {
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

            var originalDocument = currentSolution.GetDocument(document.Id) ?? document;
            if (originalDocument is SourceGeneratedDocument)
                continue;

            var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
            if (originalRoot == null)
                continue;

            var workingDocument = originalDocument;
            var root = originalRoot;
            var semanticModel = await workingDocument.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
                continue;

            var convertedCount = 0;
            while (true)
            {
                var passReplacements = new Dictionary<SyntaxNode, SyntaxNode>();
                foreach (var expression in CollectConvertibleExpressions(root, semanticModel))
                {
                    if (TryConvert(expression, out var converted))
                        passReplacements[expression] = converted;
                }

                if (passReplacements.Count == 0)
                    break;

                convertedCount += passReplacements.Count;
                var passRoot = root.ReplaceNodes(passReplacements.Keys, (original, _) => passReplacements[original]);
                workingDocument = workingDocument.WithSyntaxRoot(passRoot);
                root = await workingDocument.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                    break;
                semanticModel = await workingDocument.GetSemanticModelAsync(cancellationToken);
                if (semanticModel == null)
                    break;
            }

            if (convertedCount == 0 || root == null)
                continue;

            SyntaxNode newRoot = root;
            var newDocument = workingDocument;
            var beforeText = await originalDocument.GetTextAsync(cancellationToken);
            var afterText = await newDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var span = originalRoot.GetLocation().GetLineSpan();
                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = BuildAllFilesDescription(convertedCount),
                    BeforeSnippet = originalRoot.NormalizeWhitespace().ToFullString().Trim(),
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
            ? "Convert expression to interpolated string"
            : $"Convert {convertedCount} expressions to interpolated strings";

    /// <summary>
    /// Collects every distinct convertible <c>string.Format</c> invocation and
    /// outer concatenation in <paramref name="root"/> using the same walk as
    /// <see cref="FindConvertibleExpression"/>. Inner nodes of a 3+ operand
    /// chain collapse to <see cref="OuterConcatenation"/>. Containment is
    /// applied only among expressions that can convert: an unconvertible
    /// outer Format does not hide a convertible inner Format, and a concat
    /// that contains a convertible Format is deferred so the Format is
    /// rewritten first (the allFiles walk then picks up the remaining concat).
    /// </summary>
    internal static IReadOnlyList<ExpressionSyntax> CollectConvertibleExpressions(
        SyntaxNode root,
        SemanticModel semanticModel)
    {
        var convertibleFormats = EnumerateFormatInvocations(root, semanticModel)
            .Where(invocation => TryConvert(invocation, out _))
            .ToList();

        // Innermost convertible Format wins so a nested literal Format is not
        // dropped when an outer Format cannot convert, and so ReplaceNodes
        // never has to rewrite an ancestor and descendant Format together.
        var formats = convertibleFormats
            .Where(invocation => !convertibleFormats.Any(other => other != invocation && invocation.Contains(other)))
            .Cast<ExpressionSyntax>();

        var concats = EnumerateConcatenations(root, semanticModel)
            .Select(OuterConcatenation)
            .Distinct()
            .Where(concat => TryConvert(concat, out _))
            .Where(concat => !convertibleFormats.Any(format => concat.Contains(format)));

        return formats.Concat(concats).ToList();
    }

    /// <summary>
    /// Finds a convertible <c>string.Format</c> invocation or concatenation.
    /// When <paramref name="column"/> is omitted, keeps today's first-match
    /// whose start line equals <paramref name="line"/> (Format before
    /// concatenation). When set, picks the Format invocation or the outer
    /// concatenation whose span covers that 1-based column. Format is still
    /// preferred when both kinds cover the column. Concatenation matches walk
    /// up to <see cref="OuterConcatenation"/> so a 3+ operand chain is not
    /// flattened from an inner node.
    /// </summary>
    internal static ExpressionSyntax? FindConvertibleExpression(
        SyntaxNode root,
        SemanticModel semanticModel,
        int line,
        int? column)
    {
        var formats = EnumerateFormatInvocations(root, semanticModel);
        var concats = EnumerateConcatenations(root, semanticModel);

        if (!column.HasValue)
        {
            ExpressionSyntax? firstFormat = formats.FirstOrDefault(invocation => StartsOnLine(invocation, line));
            if (firstFormat != null)
                return firstFormat;

            var firstConcat = concats.FirstOrDefault(binary => StartsOnLine(binary, line));
            return firstConcat == null ? null : OuterConcatenation(firstConcat);
        }

        var formatAtColumn = formats
            .Where(invocation => SpanCoversColumn(invocation.GetLocation().GetLineSpan(), line, column.Value))
            .OrderBy(invocation => invocation.Span.Length)
            .FirstOrDefault();
        if (formatAtColumn != null)
            return formatAtColumn;

        // Prefer the outer matching concatenation. Shortest-span pick would
        // return an inner binary of a 3+ operand chain; flattening that inner
        // node and then replacing the walked-up outer silently drops later
        // operands. Adjacent independent concatenations still do not share a
        // span, so column continues to distinguish them.
        var concatAtColumn = concats
            .Where(binary => SpanCoversColumn(binary.GetLocation().GetLineSpan(), line, column.Value))
            .OrderByDescending(binary => binary.Span.Length)
            .FirstOrDefault();
        return concatAtColumn == null ? null : OuterConcatenation(concatAtColumn);
    }

    private static IEnumerable<InvocationExpressionSyntax> EnumerateFormatInvocations(
        SyntaxNode root,
        SemanticModel semanticModel) =>
        root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsStringFormatCall(invocation, semanticModel));

    private static IEnumerable<BinaryExpressionSyntax> EnumerateConcatenations(
        SyntaxNode root,
        SemanticModel semanticModel) =>
        root.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Where(binary => binary.IsKind(SyntaxKind.AddExpression) &&
                             IsStringConcatenation(binary, semanticModel));

    /// <summary>
    /// Attempts a conversion without throwing. Used by allFiles so expressions
    /// that cannot convert stay no-ops.
    /// </summary>
    internal static bool TryConvert(ExpressionSyntax expression, out ExpressionSyntax converted)
    {
        try
        {
            if (expression is InvocationExpressionSyntax formatInvocation)
            {
                converted = ConvertFormat(formatInvocation);
                return true;
            }

            if (expression is BinaryExpressionSyntax concat)
            {
                converted = ConvertConcatenationExpression(concat);
                return true;
            }

            converted = expression;
            return false;
        }
        catch (RefactoringException)
        {
            converted = expression;
            return false;
        }
    }

    /// <summary>
    /// Walks to the outermost <c>+</c> concatenation that contains
    /// <paramref name="concat"/>. Adjacent independent concatenations are
    /// siblings, not ancestors, so this does not collapse two statements.
    /// </summary>
    internal static BinaryExpressionSyntax OuterConcatenation(BinaryExpressionSyntax concat)
    {
        var outer = concat;
        while (outer.Parent is BinaryExpressionSyntax parentBinary &&
               parentBinary.IsKind(SyntaxKind.AddExpression))
        {
            outer = parentBinary;
        }

        return outer;
    }

    private static bool StartsOnLine(SyntaxNode node, int line) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line;

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent expression also
    /// match the previous Format invocation or concatenation.
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

    private static bool IsStringFormatCall(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name.Identifier.Text == "Format")
        {
            var symbolInfo = model.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol method &&
                method.ContainingType.SpecialType == SpecialType.System_String)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStringConcatenation(BinaryExpressionSyntax binary, SemanticModel model)
    {
        var typeInfo = model.GetTypeInfo(binary);
        return typeInfo.Type?.SpecialType == SpecialType.System_String;
    }

    private async Task<RefactoringResult> ConvertStringFormat(
        Guid operationId, Document document, SyntaxNode root,
        InvocationExpressionSyntax invocation, ConvertToInterpolatedStringParams @params,
        CancellationToken cancellationToken)
    {
        var interpolatedString = ConvertFormat(invocation);

        var before = invocation.NormalizeWhitespace().ToFullString();
        var after = interpolatedString.NormalizeWhitespace().ToFullString();

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile!,
                    ChangeType = ChangeKind.Modify,
                    Description = "Convert string.Format to interpolated string",
                    BeforeSnippet = before,
                    AfterSnippet = after
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newRoot = root.ReplaceNode(invocation, interpolatedString);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            null, 0, 0);
    }

    private async Task<RefactoringResult> ConvertConcatenation(
        Guid operationId, Document document, SyntaxNode root,
        BinaryExpressionSyntax concat, ConvertToInterpolatedStringParams @params,
        CancellationToken cancellationToken)
    {
        var interpolatedString = ConvertConcatenationExpression(concat);
        var outerConcat = OuterConcatenation(concat);

        var before = outerConcat.NormalizeWhitespace().ToFullString();
        var after = interpolatedString.NormalizeWhitespace().ToFullString();

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile!,
                    ChangeType = ChangeKind.Modify,
                    Description = "Convert string concatenation to interpolated string",
                    BeforeSnippet = before.Length > 200 ? before[..200] + "..." : before,
                    AfterSnippet = after.Length > 200 ? after[..200] + "..." : after
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newRoot = root.ReplaceNode(outerConcat, interpolatedString);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            null, 0, 0);
    }

    private static InterpolatedStringExpressionSyntax ConvertFormat(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 1)
            throw new RefactoringException(ErrorCodes.CannotConvert, "string.Format must have at least one argument.");

        var formatArg = args[0].Expression;
        if (formatArg is not LiteralExpressionSyntax formatLiteral ||
            !formatLiteral.IsKind(SyntaxKind.StringLiteralExpression))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "First argument to string.Format must be a string literal.");
        }

        var formatString = formatLiteral.Token.ValueText;
        var formatArgs = args.Skip(1).Select(a => a.Expression).ToList();

        // Build interpolated string
        var contents = new List<InterpolatedStringContentSyntax>();
        var i = 0;
        while (i < formatString.Length)
        {
            // Find next format placeholder {N} or {N:format}
            var braceIndex = formatString.IndexOf('{', i);
            if (braceIndex == -1)
            {
                // Rest is text
                if (i < formatString.Length)
                    contents.Add(SyntaxFactory.InterpolatedStringText(
                        SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken,
                            formatString[i..], formatString[i..], SyntaxTriviaList.Empty)));
                break;
            }

            // Escaped braces {{ or }}
            if (braceIndex + 1 < formatString.Length && formatString[braceIndex + 1] == '{')
            {
                contents.Add(SyntaxFactory.InterpolatedStringText(
                    SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken,
                        formatString[i..(braceIndex + 2)], formatString[i..(braceIndex + 2)], SyntaxTriviaList.Empty)));
                i = braceIndex + 2;
                continue;
            }

            // Add text before the brace
            if (braceIndex > i)
            {
                contents.Add(SyntaxFactory.InterpolatedStringText(
                    SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken,
                        formatString[i..braceIndex], formatString[i..braceIndex], SyntaxTriviaList.Empty)));
            }

            // Parse {N} or {N:format}
            var closeBrace = formatString.IndexOf('}', braceIndex);
            if (closeBrace == -1) break;

            var placeholder = formatString[(braceIndex + 1)..closeBrace];
            var colonIndex = placeholder.IndexOf(':');
            var indexStr = colonIndex >= 0 ? placeholder[..colonIndex] : placeholder;

            if (int.TryParse(indexStr, out var argIndex) && argIndex < formatArgs.Count)
            {
                InterpolationSyntax interpolation;
                if (colonIndex >= 0)
                {
                    var formatSpec = placeholder[(colonIndex + 1)..];
                    interpolation = SyntaxFactory.Interpolation(
                        formatArgs[argIndex],
                        null,
                        SyntaxFactory.InterpolationFormatClause(
                            SyntaxFactory.Token(SyntaxKind.ColonToken),
                            SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken,
                                formatSpec, formatSpec, SyntaxTriviaList.Empty)));
                }
                else
                {
                    interpolation = SyntaxFactory.Interpolation(formatArgs[argIndex]);
                }
                contents.Add(interpolation);
            }

            i = closeBrace + 1;
        }

        return SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(contents));
    }

    private static InterpolatedStringExpressionSyntax ConvertConcatenationExpression(BinaryExpressionSyntax concat)
    {
        // Flatten the outermost concatenation so a column (or omitted first
        // match) on an inner operand of a 3+ chain still keeps later parts.
        var outerConcat = OuterConcatenation(concat);
        var parts = FlattenConcatenation(outerConcat);

        var contents = new List<InterpolatedStringContentSyntax>();
        foreach (var part in parts)
        {
            if (part is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                var text = literal.Token.ValueText;
                if (!string.IsNullOrEmpty(text))
                {
                    contents.Add(SyntaxFactory.InterpolatedStringText(
                        SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken,
                            text, text, SyntaxTriviaList.Empty)));
                }
            }
            else
            {
                contents.Add(SyntaxFactory.Interpolation(part));
            }
        }

        return SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(contents));
    }

    private static List<ExpressionSyntax> FlattenConcatenation(ExpressionSyntax expr)
    {
        var parts = new List<ExpressionSyntax>();

        if (expr is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
        {
            parts.AddRange(FlattenConcatenation(binary.Left));
            parts.AddRange(FlattenConcatenation(binary.Right));
        }
        else
        {
            parts.Add(expr);
        }

        return parts;
    }
}

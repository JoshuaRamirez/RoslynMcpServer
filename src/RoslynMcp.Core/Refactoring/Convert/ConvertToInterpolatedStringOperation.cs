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
/// Converts string.Format() calls and string concatenation to interpolated strings.
/// </summary>
public sealed class ConvertToInterpolatedStringOperation : RefactoringOperationBase<ConvertToInterpolatedStringParams>
{
    /// <inheritdoc />
    public ConvertToInterpolatedStringOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToInterpolatedStringParams @params)
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
        ConvertToInterpolatedStringParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var targetLine = @params.Line - 1;

        // Try string.Format first
        var formatInvocation = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.GetLocation().GetLineSpan().StartLinePosition.Line == targetLine &&
                                  IsStringFormatCall(i, semanticModel));

        if (formatInvocation != null)
        {
            return await ConvertStringFormat(operationId, document, root, formatInvocation, @params, cancellationToken);
        }

        // Try concatenation
        var concat = root.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .FirstOrDefault(b => b.GetLocation().GetLineSpan().StartLinePosition.Line == targetLine &&
                                  b.IsKind(SyntaxKind.AddExpression) &&
                                  IsStringConcatenation(b, semanticModel));

        if (concat != null)
        {
            return await ConvertConcatenation(operationId, document, root, concat, @params, cancellationToken);
        }

        throw new RefactoringException(ErrorCodes.CannotConvert,
            $"No string.Format() call or string concatenation found at line {@params.Line}.");
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

        var interpolatedString = SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(contents));

        var before = invocation.NormalizeWhitespace().ToFullString();
        var after = interpolatedString.NormalizeWhitespace().ToFullString();

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
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
        // Flatten the concatenation tree
        var parts = FlattenConcatenation(concat);

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

        var interpolatedString = SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(contents));

        // Find the outermost concatenation expression
        var outerConcat = concat;
        while (outerConcat.Parent is BinaryExpressionSyntax parentBinary &&
               parentBinary.IsKind(SyntaxKind.AddExpression))
        {
            outerConcat = parentBinary;
        }

        var before = outerConcat.NormalizeWhitespace().ToFullString();
        var after = interpolatedString.NormalizeWhitespace().ToFullString();

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
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

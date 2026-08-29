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
/// Converts an expression-bodied member (<c>=&gt; expr</c>) to a block body.
/// Inverse of <see cref="ConvertExpressionBodyOperation"/> in the ToExpressionBody direction.
/// </summary>
public sealed class ConvertToBlockBodyOperation : RefactoringOperationBase<ConvertToBlockBodyParams>
{
    /// <summary>
    /// Creates a new convert-to-block-body operation.
    /// </summary>
    public ConvertToBlockBodyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToBlockBodyParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-to-block-body parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertToBlockBodyParams @params)
    {
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

    /// <summary>
    /// Rejects documents that cannot receive source edits.
    /// </summary>
    internal static void ValidateDocumentIsEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (source-generated).");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable.");
        }

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToBlockBodyParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var member = FindMember(root, @params.MemberName, @params.Line, @params.Column);
        if (member == null)
        {
            var location = @params.Column.HasValue
                ? $"{@params.MemberName ?? $"at line {@params.Line}"}, column {@params.Column.Value}"
                : @params.MemberName ?? $"at line {@params.Line}";
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"Member '{location}' not found.");
        }

        if (!IsConvertibleKind(member))
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"Member '{GetMemberName(member) ?? member.Kind().ToString()}' does not support block body conversion.");
        }

        var (newMember, beforeSnippet, afterSnippet) = ConvertToBlockBody(member);

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Convert '{GetMemberName(member)}' to block body",
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newRoot = root.ReplaceNode(member, newMember);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

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
                Name = GetMemberName(member) ?? string.Empty,
                FullyQualifiedName = GetMemberName(member) ?? string.Empty,
                Kind = MapKind(member)
            },
            0,
            0);
    }

    /// <summary>
    /// Finds a convertible member. When <paramref name="column"/> is omitted,
    /// keeps today's pick (memberName and/or line; smallest containing node).
    /// When set with <paramref name="line"/>, picks the member whose
    /// identifier or declaration span covers that 1-based column. Column
    /// without line cannot disambiguate same-indent same-name members
    /// across lines — keep today's first-match rather than substituting
    /// each candidate's own start line.
    /// </summary>
    internal static SyntaxNode? FindMember(SyntaxNode root, string? memberName, int? line, int? column)
    {
        var candidates = root.DescendantNodes()
            .Where(node => node is MemberDeclarationSyntax or LocalFunctionStatementSyntax)
            .ToList();

        IEnumerable<SyntaxNode> filtered = candidates;

        if (!string.IsNullOrWhiteSpace(memberName))
            filtered = filtered.Where(node => GetMemberName(node) == memberName);

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name member and could silently pick the shortest. Keep
        // today's FirstOrDefault after the memberName filter.
        if (column.HasValue && !line.HasValue)
            return filtered.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // signature's identifier may live on a continuation line whose
            // declaration span still covers that column.
            return filtered
                .Where(node => MemberCoversColumn(node, line!.Value, column.Value))
                .OrderBy(node => IdentifierCoversColumn(node, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(node => node.Span.Length)
                .FirstOrDefault();
        }

        if (line.HasValue)
        {
            filtered = filtered.Where(node => ContainsLine(node, line.Value));
            return filtered.OrderBy(node => node.Span.Length).FirstOrDefault();
        }

        return filtered.FirstOrDefault();
    }

    private static bool MemberCoversColumn(SyntaxNode member, int line, int column) =>
        IdentifierCoversColumn(member, line, column) ||
        SpanCoversColumn(member.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(SyntaxNode member, int line, int column)
    {
        var token = GetIdentifierToken(member);
        return token != default && SpanCoversColumn(token.GetLocation().GetLineSpan(), line, column);
    }

    private static SyntaxToken GetIdentifierToken(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
        OperatorDeclarationSyntax op => op.OperatorToken,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetFirstToken(),
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier ?? default,
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.FirstOrDefault()?.Identifier ?? default,
        TypeDeclarationSyntax type => type.Identifier,
        _ => default
    };

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent member also
    /// match the previous declaration.
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

    private static bool ContainsLine(SyntaxNode node, int line)
    {
        var span = node.GetLocation().GetLineSpan();
        var start = span.StartLinePosition.Line + 1;
        var end = span.EndLinePosition.Line + 1;
        return line >= start && line <= end;
    }

    private static bool IsConvertibleKind(SyntaxNode node) => node is
        MethodDeclarationSyntax or
        PropertyDeclarationSyntax or
        IndexerDeclarationSyntax or
        OperatorDeclarationSyntax or
        ConversionOperatorDeclarationSyntax or
        ConstructorDeclarationSyntax or
        DestructorDeclarationSyntax or
        LocalFunctionStatementSyntax or
        EventDeclarationSyntax;

    private static string? GetMemberName(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text,
        PropertyDeclarationSyntax property => property.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        OperatorDeclarationSyntax op => $"operator {op.OperatorToken.Text}",
        ConversionOperatorDeclarationSyntax => "implicit/explicit operator",
        ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
        DestructorDeclarationSyntax destructor => destructor.Identifier.Text,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier.Text,
        EventDeclarationSyntax @event => @event.Identifier.Text,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        TypeDeclarationSyntax type => type.Identifier.Text,
        _ => null
    };

    private static Contracts.Enums.SymbolKind MapKind(SyntaxNode member) => member switch
    {
        PropertyDeclarationSyntax or IndexerDeclarationSyntax => Contracts.Enums.SymbolKind.Property,
        EventDeclarationSyntax => Contracts.Enums.SymbolKind.Event,
        _ => Contracts.Enums.SymbolKind.Method
    };

    private static (SyntaxNode newNode, string before, string after) ConvertToBlockBody(SyntaxNode member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => ConvertMethod(method),
            LocalFunctionStatementSyntax localFunction => ConvertLocalFunction(localFunction),
            OperatorDeclarationSyntax op => ConvertOperator(op),
            ConversionOperatorDeclarationSyntax conversion => ConvertConversionOperator(conversion),
            ConstructorDeclarationSyntax constructor => ConvertConstructor(constructor),
            DestructorDeclarationSyntax destructor => ConvertDestructor(destructor),
            PropertyDeclarationSyntax property => ConvertProperty(property),
            IndexerDeclarationSyntax indexer => ConvertIndexer(indexer),
            EventDeclarationSyntax @event => ConvertEvent(@event),
            _ => throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "Member type does not support block body conversion.")
        };
    }

    private static (SyntaxNode newNode, string before, string after) ConvertMethod(MethodDeclarationSyntax method)
    {
        EnsureExpressionBody(method.ExpressionBody, method.Body, "Method");
        var expr = method.ExpressionBody!.Expression;
        var stmt = CreateStatement(expr, useReturn: !IsNonReturning(method.ReturnType, method.Modifiers));
        var before = FormatExpressionBody(expr);
        var newMethod = method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(stmt))
            .NormalizeWhitespace();
        return (newMethod, before, newMethod.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertLocalFunction(
        LocalFunctionStatementSyntax localFunction)
    {
        EnsureExpressionBody(localFunction.ExpressionBody, localFunction.Body, "Local function");
        var expr = localFunction.ExpressionBody!.Expression;
        var stmt = CreateStatement(expr, useReturn: !IsNonReturning(localFunction.ReturnType, localFunction.Modifiers));
        var before = FormatExpressionBody(expr);
        var converted = localFunction
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(stmt))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertOperator(OperatorDeclarationSyntax op)
    {
        EnsureExpressionBody(op.ExpressionBody, op.Body, "Operator");
        var expr = op.ExpressionBody!.Expression;
        var stmt = CreateStatement(expr, useReturn: true);
        var before = FormatExpressionBody(expr);
        var converted = op
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(stmt))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertConversionOperator(
        ConversionOperatorDeclarationSyntax conversion)
    {
        EnsureExpressionBody(conversion.ExpressionBody, conversion.Body, "Conversion operator");
        var expr = conversion.ExpressionBody!.Expression;
        var stmt = CreateStatement(expr, useReturn: true);
        var before = FormatExpressionBody(expr);
        var converted = conversion
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(stmt))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertConstructor(
        ConstructorDeclarationSyntax constructor)
    {
        EnsureExpressionBody(constructor.ExpressionBody, constructor.Body, "Constructor");
        var expr = constructor.ExpressionBody!.Expression;
        var stmt = CreateStatement(expr, useReturn: false);
        var before = FormatExpressionBody(expr);
        var converted = constructor
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(stmt))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertDestructor(
        DestructorDeclarationSyntax destructor)
    {
        EnsureExpressionBody(destructor.ExpressionBody, destructor.Body, "Destructor");
        var expr = destructor.ExpressionBody!.Expression;
        var stmt = CreateStatement(expr, useReturn: false);
        var before = FormatExpressionBody(expr);
        var converted = destructor
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(stmt))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertProperty(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody != null)
        {
            var expr = property.ExpressionBody.Expression;
            var accessor = CreateBlockAccessor(SyntaxKind.GetAccessorDeclaration, expr, useReturn: true);
            var before = FormatExpressionBody(expr);
            var newProp = property
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(accessor)))
                .NormalizeWhitespace();
            return (newProp, before, newProp.AccessorList!.ToString().Trim());
        }

        return ConvertAccessors(
            property,
            property.AccessorList,
            updated => property.WithAccessorList(updated).NormalizeWhitespace(),
            converted => converted.AccessorList!.ToString().Trim(),
            "Property");
    }

    private static (SyntaxNode newNode, string before, string after) ConvertIndexer(IndexerDeclarationSyntax indexer)
    {
        if (indexer.ExpressionBody != null)
        {
            var expr = indexer.ExpressionBody.Expression;
            var accessor = CreateBlockAccessor(SyntaxKind.GetAccessorDeclaration, expr, useReturn: true);
            var before = FormatExpressionBody(expr);
            var converted = indexer
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(accessor)))
                .NormalizeWhitespace();
            return (converted, before, converted.AccessorList!.ToString().Trim());
        }

        return ConvertAccessors(
            indexer,
            indexer.AccessorList,
            updated => indexer.WithAccessorList(updated).NormalizeWhitespace(),
            converted => converted.AccessorList!.ToString().Trim(),
            "Indexer");
    }

    private static (SyntaxNode newNode, string before, string after) ConvertEvent(EventDeclarationSyntax @event)
    {
        return ConvertAccessors(
            @event,
            @event.AccessorList,
            updated => @event.WithAccessorList(updated).NormalizeWhitespace(),
            converted => converted.AccessorList!.ToString().Trim(),
            "Event");
    }

    private static (SyntaxNode newNode, string before, string after) ConvertAccessors<T>(
        T member,
        AccessorListSyntax? accessorList,
        Func<AccessorListSyntax, T> withAccessors,
        Func<T, string> afterSnippet,
        string memberKind)
        where T : SyntaxNode
    {
        if (accessorList == null)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"{memberKind} does not have an expression body.");
        }

        var expressionBodied = accessorList.Accessors.Where(accessor => accessor.ExpressionBody != null).ToList();
        if (expressionBodied.Count == 0)
        {
            if (accessorList.Accessors.Any(accessor => accessor.Body != null))
            {
                throw new RefactoringException(
                    ErrorCodes.AlreadyBlockBody,
                    $"{memberKind} already has a block body.");
            }

            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"{memberKind} does not have an expression body.");
        }

        var before = string.Join(
            " ",
            expressionBodied.Select(accessor =>
                $"{accessor.Keyword.Text} {FormatExpressionBody(accessor.ExpressionBody!.Expression)}"));

        var convertedAccessors = accessorList.Accessors.Select(accessor =>
            accessor.ExpressionBody == null ? accessor : ConvertAccessorToBlock(accessor));

        var converted = withAccessors(accessorList.WithAccessors(SyntaxFactory.List(convertedAccessors)));
        return (converted, before, afterSnippet(converted));
    }

    private static AccessorDeclarationSyntax ConvertAccessorToBlock(AccessorDeclarationSyntax accessor)
    {
        var useReturn = accessor.IsKind(SyntaxKind.GetAccessorDeclaration);
        return accessor
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(CreateStatement(accessor.ExpressionBody!.Expression, useReturn)));
    }

    private static AccessorDeclarationSyntax CreateBlockAccessor(
        SyntaxKind kind,
        ExpressionSyntax expression,
        bool useReturn)
    {
        return SyntaxFactory.AccessorDeclaration(kind)
            .WithBody(SyntaxFactory.Block(CreateStatement(expression, useReturn)));
    }

    private static void EnsureExpressionBody(ArrowExpressionClauseSyntax? expressionBody, BlockSyntax? body, string memberKind)
    {
        if (expressionBody != null)
            return;

        if (body != null)
        {
            throw new RefactoringException(
                ErrorCodes.AlreadyBlockBody,
                $"{memberKind} already has a block body.");
        }

        throw new RefactoringException(
            ErrorCodes.CannotConvert,
            $"{memberKind} does not have an expression body.");
    }

    private static StatementSyntax CreateStatement(ExpressionSyntax expression, bool useReturn)
    {
        if (expression is ThrowExpressionSyntax throwExpression)
            return SyntaxFactory.ThrowStatement(throwExpression.Expression);

        if (useReturn)
            return SyntaxFactory.ReturnStatement(expression);

        return SyntaxFactory.ExpressionStatement(expression);
    }

    private static bool IsNonReturning(TypeSyntax returnType, SyntaxTokenList modifiers) =>
        IsVoidReturn(returnType) ||
        (modifiers.Any(SyntaxKind.AsyncKeyword) && IsNonGenericTaskLike(returnType));

    private static bool IsVoidReturn(TypeSyntax returnType) =>
        returnType is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static bool IsNonGenericTaskLike(TypeSyntax returnType) => returnType switch
    {
        GenericNameSyntax => false,
        QualifiedNameSyntax qualified => IsNonGenericTaskLike(qualified.Right),
        AliasQualifiedNameSyntax alias => IsNonGenericTaskLike(alias.Name),
        IdentifierNameSyntax identifier => IsTaskLikeName(identifier.Identifier.Text),
        _ => false
    };

    private static bool IsTaskLikeName(string name) => name is "Task" or "ValueTask";

    private static string FormatExpressionBody(ExpressionSyntax expression) =>
        $"=> {expression.NormalizeWhitespace()};";
}

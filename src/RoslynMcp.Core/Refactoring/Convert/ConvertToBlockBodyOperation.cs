using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
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
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.MemberName) || @params.Line.HasValue || @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with memberName, line, or column.");
            }

            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        ValidateSourceFilePath(@params.SourceFile);

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.MemberName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either memberName or line must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static void ValidateSourceFilePath(string sourceFile)
    {
        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToBlockBodyParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

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
                    File = @params.SourceFile!,
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
    /// Converts every eligible expression-bodied member in every C# document
    /// (same document filter as <c>ConvertExpressionBodyOperation.ExecuteAllFilesAsync</c> /
    /// <c>ConvertPropertyOperation.ExecuteAllFilesAsync</c> /
    /// <c>AddBracesOperation.ExecuteAllFilesAsync</c>: <c>FilePath</c> ends
    /// with <c>.cs</c>). Optional <c>sourceFile</c> limits the walk via
    /// <see cref="DocumentSourceFileFilter"/>. Already-block and otherwise
    /// ineligible members or documents are skipped. When every file is a
    /// no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ConvertToBlockBodyParams @params,
        CancellationToken cancellationToken)
    {
        var currentSolution = Context.Solution;
        var allDocuments = currentSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);
            if (allDocuments.Count == 0)
            {
                throw new RefactoringException(
                    ErrorCodes.SourceNotInWorkspace,
                    $"File not found in workspace: {@params.SourceFile}");
            }
        }

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDocument = currentSolution.GetDocument(document.Id) ?? document;
            if (currentDocument is SourceGeneratedDocument)
                continue;

            if (!DocumentEditableHelpers.IsDocumentEditable(currentDocument, Context.Workspace))
                continue;

            var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                continue;

            // Bottom-up rewriter so a nested convertible member (e.g. local
            // function) is rewritten before its enclosing member; returning a
            // precomputed ancestor from ReplaceNodes would discard the nested
            // conversion (Codex P2 on allFiles).
            var rewriter = new ConvertToBlockBodyAllFilesRewriter();
            var newRoot = rewriter.Visit(root)!;
            if (rewriter.ConvertedCount == 0)
                continue;

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
                    Description = BuildAllFilesDescription(rewriter.ConvertedCount),
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
            ? "Convert member to block body"
            : $"Convert {convertedCount} members to block body";


    /// <summary>
    /// Attempts a conversion without throwing. Used by allFiles so already-
    /// block and otherwise ineligible members stay no-ops.
    /// </summary>
    internal static bool TryConvert(
        SyntaxNode member,
        out SyntaxNode newMember,
        out string beforeSnippet,
        out string afterSnippet)
    {
        try
        {
            (newMember, beforeSnippet, afterSnippet) = ConvertToBlockBody(member);
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
                .Where(node => MemberCoverage.MemberCoversColumn(node, line!.Value, column.Value))
                .OrderBy(node => MemberCoverage.IdentifierCoversColumn(node, line!.Value, column.Value) ? 0 : 1)
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

    /// <summary>
    /// 1-based line coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so a span that ends at the start of a line does not
    /// cover that line. Treating the end as inclusive would let the first
    /// line of an adjacent member also match the previous declaration. Same
    /// exclusive-end idea as <c>SpanCoverage.SpanCoversLine</c>.
    /// </summary>
    internal static bool ContainsLine(SyntaxNode node, int line)
    {
        var span = node.GetLocation().GetLineSpan();
        var start = span.StartLinePosition.Line + 1;
        var end = span.EndLinePosition.Line + 1;

        if (line < start || line > end)
            return false;
        if (line == end && span.EndLinePosition.Character == 0)
            return false;
        return true;
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
        var expressionBody = method.ExpressionBody!;
        var stmt = CreateStatement(expressionBody, useReturn: !IsNonReturning(method.ReturnType, method.Modifiers));
        var before = FormatExpressionBody(expressionBody.Expression);
        var newMethod = method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(CreateBlock(stmt, method.SemicolonToken))
            .NormalizeWhitespace();
        return (newMethod, before, newMethod.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertLocalFunction(
        LocalFunctionStatementSyntax localFunction)
    {
        EnsureExpressionBody(localFunction.ExpressionBody, localFunction.Body, "Local function");
        var expressionBody = localFunction.ExpressionBody!;
        var stmt = CreateStatement(expressionBody, useReturn: !IsNonReturning(localFunction.ReturnType, localFunction.Modifiers));
        var before = FormatExpressionBody(expressionBody.Expression);
        var converted = localFunction
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(CreateBlock(stmt, localFunction.SemicolonToken))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertOperator(OperatorDeclarationSyntax op)
    {
        EnsureExpressionBody(op.ExpressionBody, op.Body, "Operator");
        var expressionBody = op.ExpressionBody!;
        var stmt = CreateStatement(expressionBody, useReturn: true);
        var before = FormatExpressionBody(expressionBody.Expression);
        var converted = op
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(CreateBlock(stmt, op.SemicolonToken))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertConversionOperator(
        ConversionOperatorDeclarationSyntax conversion)
    {
        EnsureExpressionBody(conversion.ExpressionBody, conversion.Body, "Conversion operator");
        var expressionBody = conversion.ExpressionBody!;
        var stmt = CreateStatement(expressionBody, useReturn: true);
        var before = FormatExpressionBody(expressionBody.Expression);
        var converted = conversion
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(CreateBlock(stmt, conversion.SemicolonToken))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertConstructor(
        ConstructorDeclarationSyntax constructor)
    {
        EnsureExpressionBody(constructor.ExpressionBody, constructor.Body, "Constructor");
        var expressionBody = constructor.ExpressionBody!;
        var stmt = CreateStatement(expressionBody, useReturn: false);
        var before = FormatExpressionBody(expressionBody.Expression);
        var converted = constructor
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(CreateBlock(stmt, constructor.SemicolonToken))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertDestructor(
        DestructorDeclarationSyntax destructor)
    {
        EnsureExpressionBody(destructor.ExpressionBody, destructor.Body, "Destructor");
        var expressionBody = destructor.ExpressionBody!;
        var stmt = CreateStatement(expressionBody, useReturn: false);
        var before = FormatExpressionBody(expressionBody.Expression);
        var converted = destructor
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(CreateBlock(stmt, destructor.SemicolonToken))
            .NormalizeWhitespace();
        return (converted, before, converted.Body!.ToString().Trim());
    }

    private static (SyntaxNode newNode, string before, string after) ConvertProperty(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody != null)
        {
            var expressionBody = property.ExpressionBody;
            var accessor = CreateBlockAccessor(SyntaxKind.GetAccessorDeclaration, expressionBody, useReturn: true);
            var before = FormatExpressionBody(expressionBody.Expression);
            var accessorList = AttachSemicolonTrailingTrivia(
                SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(accessor)),
                property.SemicolonToken);
            var newProp = property
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(accessorList)
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
            var expressionBody = indexer.ExpressionBody;
            var accessor = CreateBlockAccessor(SyntaxKind.GetAccessorDeclaration, expressionBody, useReturn: true);
            var before = FormatExpressionBody(expressionBody.Expression);
            var accessorList = AttachSemicolonTrailingTrivia(
                SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(accessor)),
                indexer.SemicolonToken);
            var converted = indexer
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(accessorList)
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
            .WithBody(CreateBlock(
                CreateStatement(accessor.ExpressionBody!, useReturn),
                accessor.SemicolonToken));
    }

    /// <summary>
    /// Builds a block body and moves leading + trailing trivia from the removed
    /// expression-body semicolon onto the closing brace so comments like
    /// <c>=> 1; // explanation</c> and next-line <c>/* explanation */;</c>
    /// survive conversion (including allFiles).
    /// </summary>
    private static BlockSyntax CreateBlock(StatementSyntax statement, SyntaxToken semicolonToken)
    {
        var block = SyntaxFactory.Block(statement);
        return block.WithCloseBraceToken(
            AttachSemicolonTrivia(block.CloseBraceToken, semicolonToken));
    }

    /// <summary>
    /// Same semicolon-trivia transfer for expression-bodied properties/indexers
    /// that become an accessor list instead of a method body.
    /// </summary>
    private static AccessorListSyntax AttachSemicolonTrailingTrivia(
        AccessorListSyntax accessorList,
        SyntaxToken semicolonToken)
    {
        return accessorList.WithCloseBraceToken(
            AttachSemicolonTrivia(accessorList.CloseBraceToken, semicolonToken));
    }

    private static SyntaxToken AttachSemicolonTrivia(SyntaxToken closeBrace, SyntaxToken semicolonToken)
    {
        var leading = semicolonToken.LeadingTrivia;
        var trailing = semicolonToken.TrailingTrivia;
        if (leading.Count == 0 && trailing.Count == 0)
            return closeBrace;

        if (leading.Count > 0)
            closeBrace = closeBrace.WithLeadingTrivia(closeBrace.LeadingTrivia.AddRange(leading));
        if (trailing.Count > 0)
            closeBrace = closeBrace.WithTrailingTrivia(closeBrace.TrailingTrivia.AddRange(trailing));
        return closeBrace;
    }

    private static AccessorDeclarationSyntax CreateBlockAccessor(
        SyntaxKind kind,
        ArrowExpressionClauseSyntax expressionBody,
        bool useReturn)
    {
        return SyntaxFactory.AccessorDeclaration(kind)
            .WithBody(SyntaxFactory.Block(CreateStatement(expressionBody, useReturn)));
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

    /// <summary>
    /// Builds the block statement and keeps non-whitespace trivia from the
    /// removed <c>=&gt;</c> token (e.g. <c>=&gt; /* rationale */ 1</c>) on the
    /// statement so comments survive conversion (including allFiles).
    /// </summary>
    private static StatementSyntax CreateStatement(ArrowExpressionClauseSyntax expressionBody, bool useReturn)
    {
        var statement = CreateStatement(expressionBody.Expression, useReturn);
        return AttachArrowTrivia(statement, expressionBody.ArrowToken);
    }

    private static StatementSyntax CreateStatement(ExpressionSyntax expression, bool useReturn)
    {
        if (expression is ThrowExpressionSyntax throwExpression)
            return CreateThrowStatement(throwExpression);

        if (useReturn)
            return SyntaxFactory.ReturnStatement(expression);

        return SyntaxFactory.ExpressionStatement(expression);
    }

    /// <summary>
    /// Builds a throw statement and keeps trivia from the removed throw
    /// expression keyword (e.g. <c>throw /* reason */ new Exception()</c>)
    /// so comments survive conversion (including allFiles).
    /// </summary>
    private static ThrowStatementSyntax CreateThrowStatement(ThrowExpressionSyntax throwExpression)
    {
        var statement = SyntaxFactory.ThrowStatement(throwExpression.Expression);
        var leading = NonWhitespaceTrivia(throwExpression.ThrowKeyword.LeadingTrivia).ToArray();
        var trailing = NonWhitespaceTrivia(throwExpression.ThrowKeyword.TrailingTrivia).ToArray();
        if (leading.Length == 0 && trailing.Length == 0)
            return statement;

        var keyword = statement.ThrowKeyword;
        if (leading.Length > 0)
            keyword = keyword.WithLeadingTrivia(
                SyntaxFactory.TriviaList(leading).AddRange(keyword.LeadingTrivia));
        if (trailing.Length > 0)
            keyword = keyword.WithTrailingTrivia(
                SyntaxFactory.TriviaList(trailing).AddRange(keyword.TrailingTrivia));
        return statement.WithThrowKeyword(keyword);
    }

    private static StatementSyntax AttachArrowTrivia(StatementSyntax statement, SyntaxToken arrowToken)
    {
        var arrowTrivia = NonWhitespaceTrivia(arrowToken.LeadingTrivia)
            .Concat(NonWhitespaceTrivia(arrowToken.TrailingTrivia))
            .ToArray();
        if (arrowTrivia.Length == 0)
            return statement;

        return statement.WithLeadingTrivia(
            SyntaxFactory.TriviaList(arrowTrivia).AddRange(statement.GetLeadingTrivia()));
    }

    private static IEnumerable<SyntaxTrivia> NonWhitespaceTrivia(SyntaxTriviaList trivia) =>
        trivia.Where(item => !item.IsKind(SyntaxKind.WhitespaceTrivia)
            && !item.IsKind(SyntaxKind.EndOfLineTrivia));

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

    /// <summary>
    /// Bottom-up allFiles rewriter: converts each eligible member after its
    /// descendants, so nested conversions are not discarded by an ancestor
    /// replacement.
    /// </summary>
    private sealed class ConvertToBlockBodyAllFilesRewriter : CSharpSyntaxRewriter
    {
        public int ConvertedCount { get; private set; }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            var visited = base.Visit(node);
            if (visited == null || !IsConvertibleKind(visited))
                return visited;

            if (!TryConvert(visited, out var converted, out _, out _))
                return visited;

            ConvertedCount++;
            return converted;
        }
    }
}
